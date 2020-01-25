using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CachingFramework.Redis;
using i18n.Domain.Abstract;
using i18n.Domain.Concrete;
using i18n.Domain.Entities;
using i18n.Helpers;

namespace i18n
{
    /// <summary>
    /// A service for retrieving localized text from PO resource files
    /// </summary>
    public class TextLocalizer : ITextLocalizer
    {
        private i18nSettings _settings;

        private ITranslationRepository _translationRepository;

        private readonly RedisContext redisContext;

        private readonly string environment;

        private static Regex unicodeMatchRegex = new Regex(@"\\U(?<Value>[0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public TextLocalizer(
            string environment,
            RedisContext redisContext,
            i18nSettings settings,
            ITranslationRepository translationRepository)
        {
            this.environment = environment;
            this.redisContext = redisContext;
            _settings = settings;
            _translationRepository = translationRepository;
        }


        #region [ITextLocalizer]

        public virtual ConcurrentDictionary<string, LanguageTag> GetAppLanguages()
        {
            ConcurrentDictionary<string, LanguageTag> AppLanguages = redisContext.Cache.GetObject<ConcurrentDictionary<string, LanguageTag>>("i18n.AppLanguages");
            if (AppLanguages != null)
            {
                return AppLanguages;
            }
            lock (Sync)
            {
                AppLanguages = redisContext.Cache.GetObject<ConcurrentDictionary<string, LanguageTag>>("i18n.AppLanguages");
                if (AppLanguages != null)
                {
                    return AppLanguages;
                }

                AppLanguages = new ConcurrentDictionary<string, LanguageTag>();

                // Populate the collection.
                List<string> languages = _translationRepository.GetAvailableLanguages().Select(x => x.LanguageShortTag).ToList();

                // Ensure default language is included in AppLanguages where appropriate.
                if (LocalizedApplication.Current.MessageKeyIsValueInDefaultLanguage
                    && !languages.Any(x => LocalizedApplication.Current.DefaultLanguageTag.Equals(x)))
                {
                    languages.Add(LocalizedApplication.Current.DefaultLanguageTag.ToString());
                }

                foreach (var langtag in languages)
                {
                    if (IsLanguageValid(langtag))
                    {
                        AppLanguages[langtag] = LanguageTag.GetCachedInstance(langtag);
                    }
                }

                redisContext.Cache.SetObject("i18n.AppLanguages", AppLanguages);

                // Done.
                return AppLanguages;
            }
        }

        public virtual string GetText(string msgid, string msgcomment, LanguageItem[] languages, out LanguageTag o_langtag, int maxPasses = -1)
        {
            // Validate arguments.
            if (maxPasses > (int)LanguageTag.MatchGrade._MaxMatch + 1)
            {
                maxPasses = (int)LanguageTag.MatchGrade._MaxMatch + 1;
            }
            // Init.
            bool fallbackOnDefault = maxPasses == (int)LanguageTag.MatchGrade._MaxMatch + 1
                || maxPasses == -1;
            // Determine the key for the msg lookup. This may be either msgid or msgid+msgcomment, depending on the prevalent
            // MessageContextEnabledFromComment setting.
            string msgkey = msgid == null ?
                msgid :
                TemplateItem.KeyFromMsgidAndComment(msgid, msgcomment, _settings.MessageContextEnabledFromComment);
            // Perform language matching based on UserLanguages, AppLanguages, and presence of
            // resource under msgid for any particular AppLanguage.
            string text;
            o_langtag = LanguageMatching.MatchLists(
                languages,
                GetAppLanguages().Values,
                msgkey,
                TryGetTextFor,
                out text,
                Math.Min(maxPasses, (int)LanguageTag.MatchGrade._MaxMatch));
            // If match was successfull
            if (text != null)
            {
                // If the msgkey was returned...don't output that but rather the msgid as the msgkey
                // may be msgid+msgcomment.
                if (text == msgkey)
                {
                    return msgid;
                }
                return text;
            }
            // Optionally try default language.
            if (fallbackOnDefault)
            {
                o_langtag = LocalizedApplication.Current.DefaultLanguageTag;
                return msgid;
            }
            //
            return null;
        }

        #endregion

        internal readonly object Sync = new object();

        /// <summary>
        /// Assesses whether a language is PO-valid, that is whether or not one or more
        /// localized messages exists for the language.
        /// </summary>
        /// <returns>true if one or more localized messages exist for the language; otherwise false.</returns>
        private bool IsLanguageValid(string langtag)
        {
            // Note that there is no need to serialize access to System.Web.HttpRuntime.Cache when just reading from it.
            //
            if (!langtag.IsSet())
            {
                return false;
            }

            // Default language is always valid.
            if (LocalizedApplication.Current.MessageKeyIsValueInDefaultLanguage
                && LocalizedApplication.Current.DefaultLanguageTag.Equals(langtag))
            {
                return true;
            }

            ConcurrentDictionary<string, TranslationItem> messages = redisContext.Cache.GetObject<ConcurrentDictionary<string, TranslationItem>>(GetCacheKey(langtag));

            // If messages not yet loaded in for the language
            if (messages == null)
            {
                return _translationRepository.TranslationExists(langtag);
            }

            return true;
        }

        /// <summary>
        /// Lookup whether any messages exist for the passed langtag, and if so attempts
        /// to lookup the message for the passed msgid, or if the msgid is null returns indication
        /// of whether any messages exist for the langtag.
        /// </summary>
        /// <param name="langtag">
        /// Language tag of the subject langtag.
        /// </param>
        /// <param name="msgkey">
        /// Key of the message to lookup, or null to test for any message loaded for the langtag.
        /// When on-null, the format of the key is as generated by the TemplateItem.KeyFromMsgidAndComment
        /// helper.
        /// </param>
        /// <returns>
        /// On success, returns the translated message, or if msgkey is null returns an empty string ("")
        /// to indciate that one or more messages exist for the langtag.
        /// On failure, returns null.
        /// </returns>
        private string TryGetTextFor(string langtag, string msgkey)
        {
            // If no messages loaded for language...fail.
            if (!IsLanguageValid(langtag))
            {
                return null;
            }

            // If not testing for a specific message, that is just testing whether any messages 
            // are present...return positive.
            if (msgkey == null)
            {
                return "";
            }

            // Lookup specific message text in the language PO and if found...return that.
            string text = LookupText(langtag, msgkey);
            if (text == null && unicodeMatchRegex.IsMatch(msgkey))
            {
                // If message was not found but contains escaped unicode characters, try converting those
                // to characters and look up the message again
                var msgkeyClean = unicodeMatchRegex.Replace(msgkey, m =>
                {
                    var code = int.Parse(m.Groups["Value"].Value, NumberStyles.HexNumber);
                    return char.ConvertFromUtf32(code);
                });
                text = LookupText(langtag, msgkeyClean);
            }

            if (text != null)
            {
                return text;
            }

            // If we are looking up in the default language, and the message keys describe values
            // in that language...return the msgkey.
            if (LocalizedApplication.Current.DefaultLanguageTag.Equals(langtag)
                && LocalizedApplication.Current.MessageKeyIsValueInDefaultLanguage)
            {
                return msgkey;
            }

            // Lookup failed.
            return null;
        }

        private bool LoadMessagesIntoCache(string langtag)
        {
            lock (Sync)
            {
                // It is possible for multiple threads to race to this method. The first to
                // enter the above lock will insert the messages into the cache.
                // If we lost the race...no need to duplicate the work of the winning thread.
                if (redisContext.Cache.GetObject<ConcurrentDictionary<string, TranslationItem>>(GetCacheKey(langtag)) != null)
                {
                    return true;
                }

                Translation t = _translationRepository.GetTranslation(langtag);

                redisContext.Cache.SetObject(GetCacheKey(langtag), t.Items);
            }
            return true;
        }

        /// <returns>null if not found.</returns>
        private string LookupText(string langtag, string msgkey)
        {
            // Note that there is no need to serialize access to System.Web.HttpRuntime.Cache when just reading from it.
            //
            var messages = redisContext.Cache.GetObject<ConcurrentDictionary<string, TranslationItem>>(GetCacheKey(langtag));
            TranslationItem message = null;

            //we need to populate the cache
            if (messages == null)
            {
                LoadMessagesIntoCache(langtag);
                messages = redisContext.Cache.GetObject<ConcurrentDictionary<string, TranslationItem>>(GetCacheKey(langtag));
            }

            // Normalize any CRLF in the msgid i.e. to just LF.
            // PO only support LF so we expect strings to be stored in the repo in that form.
            // NB: we test Contains before doing Replace in case string.Replace allocs a new
            // string even on no change. (This method is called very often.)
            if (msgkey.Contains("\r\n"))
            {
                msgkey = msgkey.Replace("\r\n", "\n");
            }

            if (messages == null
                || !messages.TryGetValue(msgkey, out message)
                || !message.Message.IsSet())
            {
                return null;
            }

            return message.Message;
        }

        /// <returns>null if not found.</returns>
        private static CultureInfo GetCultureInfoFromLanguage(string language)
        {
            // TODO: replace usage of CultureInfo with the LanguageTag class.
            // This method and the use of CultureInfo is now surpassed by the LanguageTag class,
            // thus making this method of handling language tags redundant.
            //
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }
            try
            {
                var semiColonIndex = language.IndexOf(';');
                language = semiColonIndex > -1 ? language.Substring(0, semiColonIndex) : language;
                language = System.Globalization.CultureInfo.CreateSpecificCulture(language).Name;
                return new CultureInfo(language, true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string GetCacheKey(string langtag)
        {
            //return string.Format("po:{0}", langtag).ToLowerInvariant();
            // The above will cause a new string to be allocated.
            // So subsituted with the following code.

            // Obtain the cache key without allocating a new string (except for first time).
            // As this method is a high-frequency method, the overhead in the (lock-free) dictionary 
            // lookup is thought to outweigh the potentially large number of temporary string allocations
            // and the consequently hastened garbage collections.
            return environment + "." + LanguageTag.GetCachedInstance(langtag).GlobalKey;
        }
    }
}
