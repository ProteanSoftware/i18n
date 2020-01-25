using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using CachingFramework.Redis;
using i18n.Domain.Abstract;
using i18n.Domain.Concrete;
using i18n.Helpers;

namespace i18n
{
    public class DefaultRootServices : IRootServices
    {
        private static object syncRoot = new object();
        private Lazy<POTranslationRepository> translationRepository;
        private Lazy<TextLocalizer> textLocalizer;
        private Lazy<NuggetLocalizer> nuggetLocalizer;

        public DefaultRootServices(string environment, RedisContext redisContext, IDictionary<string, string> config)
        {
            if (string.IsNullOrEmpty(environment))
            {
                throw new ArgumentNullException(nameof(environment));
            }

            if (redisContext == null)
            {
                throw new ArgumentNullException(nameof(redisContext));
            }

            // Use Lazy to delay the creation of objects to when a request is being processed.
            // When initializing the app thehi may throw "Request is not available in this context" from WebConfigService
            var settings = new i18nSettings(new DictionaryConfigSettingService(config));
            translationRepository = new Lazy<POTranslationRepository>(() => new POTranslationRepository(settings));
            textLocalizer = new Lazy<TextLocalizer>(() => new TextLocalizer(environment, redisContext, settings, TranslationRepositoryForApp));
            nuggetLocalizer = new Lazy<NuggetLocalizer>(() => new NuggetLocalizer(settings, TextLocalizerForApp));
        }

        #region [IRootServices]

        public ITranslationRepository TranslationRepositoryForApp
        {
            get
            {
                return translationRepository.Value;
            }
        }

        public ITextLocalizer TextLocalizerForApp
        {
            get
            {
                return textLocalizer.Value;
            }
        }

        public INuggetLocalizer NuggetLocalizerForApp
        {
            get
            {
                return nuggetLocalizer.Value;
            }
        }

        #endregion

    }

    /// <summary>
    /// Manages the configuration of the i18n features of your localized application.
    /// </summary>
    public class LocalizedApplication : IRootServices
    {

        #region [IRootServices]

        public ITranslationRepository TranslationRepositoryForApp { get { return RootServices.TranslationRepositoryForApp; } }
        public ITextLocalizer TextLocalizerForApp { get { return RootServices.TextLocalizerForApp; } }
        public INuggetLocalizer NuggetLocalizerForApp { get { return RootServices.NuggetLocalizerForApp; } }

        #endregion

        /// <summary>
        /// The language to be used as the default for the application where no
        /// explicit language is specified or determined for a request. Defaults to "en".
        /// </summary>
        /// <remarks>
        /// When MessageKeyIsValueInDefaultLanguage is true, GetText may interpret
        /// the message keys to be message values in the DefaultLanguage (where
        /// no explicit message value is defined in the DefaultLanguage) and so
        /// output the message key.<br/>
        /// The DefaultLanguage is used in Url Localization Scheme2 for the default URL.<br/>
        /// Supports a subset of BCP 47 language tag spec corresponding to the Windows
        /// support for language names, namely the following subtags:
        ///     language (mandatory, 2 alphachars)
        ///     script   (optional, 4 alphachars)
        ///     region   (optional, 2 alphachars | 3 decdigits)
        /// Example tags supported:
        ///     "en"            [language]
        ///     "en-US"         [language + region]
        ///     "zh"            [language]
        ///     "zh-HK"         [language + region]
        ///     "zh-123"        [language + region]
        ///     "zh-Hant"       [language + script]
        ///     "zh-Hant-HK"    [language + script + region]
        ///     "zh-Hant-HK-x-ABCD"    [language + script + region + private use]
        /// </remarks>
        public string DefaultLanguage
        {
            get
            {
                return DefaultLanguageTag.ToString();
            }
            set
            {
                LanguageTag defaultLanguageTag = LanguageTag.GetCachedInstance(value);
                if (defaultLanguageTag != null) {
                    DefaultLanguageTag = defaultLanguageTag; }
            }
        }
        public LanguageTag DefaultLanguageTag { get; private set; }

        /// <summary>
        /// Specifies whether the key for a message may be assumed to be the value for
        /// the message in the default language. Defaults to true.
        /// </summary>
        /// <remarks>
        /// When true, the i18n GetText method will take it that a translation exists
        /// for all messages in the default language, even though in reality a translation
        /// is not present for the message in the default language's PO file.<br/>
        /// When false, an explicit translation is required in the default language. Typically
        /// this can be useful where keys are not strings to be output but rather codes or mnemonics
        /// of some kind.
        /// </remarks>
        public bool MessageKeyIsValueInDefaultLanguage { get; set; }

        /// <summary>
        /// The ASP.NET application's virtual application root path on the server,
        /// used by Url Localization.
        /// </summary>
        /// <remarks>
        /// This is set by the ctor automatically to the ApplicationPath of
        /// HttpContext.Current, when available. Should that not be available
        /// then the value defaults to "/".<br/>
        /// In situations where the application is configured to run under a virtual folder
        /// and you init this class in such a way that HttpContext.Current is not
        /// available, it will be necessary to set this correctly manually to the application
        /// root path.<br/>
        /// E.g. if the application root url is "example.com/MySite",
        /// set this to "/MySite". It is important that the string starts with a forward slash path separator
        /// and does NOT end with a forward slash.
        /// </remarks>
        public string ApplicationPath { get; set; }

        /// <summary>
        /// The name for the i18n cookie. Defaults to "i18n.langtag".
        /// </summary>
        public string CookieName { get; set; }

        /// <summary>
        /// Declares a method type for handling the setting of the language.
        /// </summary>
        /// <param name="context">Current http context.</param>
        /// <param name="langtag">Language being set.</param>
        public delegate void SetLanguageHandler(ILanguageTag langtag);

        /// <summary>
        /// Describes one or more procedures to be called when the principal application
        /// language (PAL) is set for an HTTP request.
        /// </summary>
        /// <remarks>
        /// A default handlers is installed which applies the PAL setting to both the
        /// CurrentCulture and CurrentUICulture settings of the current thread.
        /// This behaviour can be altered by removing (nulling) the value of this property
        /// or replacing with a new delegate.
        /// </remarks>
        public SetLanguageHandler SetPrincipalAppLanguageForRequestHandlers { get; set; }

        /// <summary>
        /// Declares a method type for a custom method called after a nugget has been translated
        /// that allows the resulting message to be modified.
        /// </summary>
        /// <remarks>
        /// In general it is good practice to postpone the escaping of characters until they
        /// are about to be displayed and then according to the content type of the output.
        /// Thus, a single quote character need not be escaped if in JSON, but should be escaped
        /// if in HTML or Javascript.
        /// This method allows for such conditional modification of the message.
        /// </remarks>
        /// <param name="context">Current http context.</param>
        /// <param name="nugget">The subject nugget being translated.</param>
        /// <param name="langtag">Language being set.</param>
        /// <param name="message">The message string which may be modified.</param>
        /// <returns>
        /// Modified message string (or message if no modification).
        /// </returns>
        public delegate string TweakMessageTranslationProc(Nugget nugget, LanguageTag langtag, string message);

        /// <summary>
        /// Registers a custom method called after a nugget has been translated
        /// that allows the resulting message to be modified.
        /// </summary>
        /// <remarks>
        /// In general it is good practice to postpone the escaping of characters until they
        /// are about to be displayed and then according to the content type of the output.
        /// This, a single quote character need not be escaped if in JSON, but should be escaped
        /// if in HTML or Javascript.
        /// This method allows for such conditional modification of the message.
        /// </remarks>
        public TweakMessageTranslationProc TweakMessageTranslation { get; set; }

        /// <summary>
        /// Specifies the type of HTTP redirect to be issued by automatic language routing:
        /// true for 301 (permanent) redirects; false for 302 (temporary) ones.
        /// Defaults to false.
        /// </summary>
        public bool PermanentRedirects { get; set; }

        /// <summary>
        /// Regular expression that controls the ContextTypes elligible for response localization.
        /// </summary>
        /// <remarks>
        /// Set to null to disable Late URL Localization.<br/>
        /// Defaults to @"^(?:(?:(?:text|application)/(?:plain|html|xml|javascript|x-javascript|json|x-json))(?:\s*;.*)?)$.<br/>
        /// Client may customise this member, for instance in Application_Start.<br/>
        /// This feature requires the LocalizedModule HTTP module to be intalled in web.config.<br/>
        /// Explanation of the default regex:<br/>
        ///  Content-type string must begin with "text" or "application"<br/>
        ///  This must be followed by "/"<br/>
        ///  This must be followed by "plain" or "html" ...<br/>
        ///  And finally this may be followed by the following sequence:<br/>
        ///      zero or more whitespace then ";" then any number of any chars up to end of string.
        /// </remarks>
        public Regex ContentTypesToLocalize = new Regex(@"^(?:(?:(?:text|application)/(?:plain|html|xml|javascript|x-javascript|json|x-json))(?:\s*;.*)?)$");

        /// <summary>
        /// Regular expression that excludes certain URL paths from being localized.
        /// </summary>
        /// <remarks>
        /// Defaults to excluding all less and css files and any URLs containing the phrases i18nSkip, glimpse, trace or elmah (case-insensitive)<br/>
        /// Clients may customise this member in Application_Start<br/>
        /// This feature requires the LocalizedModule HTTP module to be intalled in web.config.<br/>
        /// </remarks>
        public Regex UrlsToExcludeFromProcessing = new Regex(@"(?:\.(?:less|css)(?:\?|$))|(?i:i18nSkip|glimpse|trace|elmah)");

        /// <summary>
        /// Comma separated value string that lists the async postback types that should be localized.
        /// </summary>
        /// <remarks>
        /// Defaults to "updatePanel,scriptStartupBlock,pageTitle"<br/>
        /// Clients may customise this member in Application_Start.<br/>
        /// </remarks>
        public string AsyncPostbackTypesToTranslate = "updatePanel,scriptStartupBlock,pageTitle";

        public LocalizedApplication(string environment, RedisContext redisContext, IDictionary<string, string> config)
        {
            // Default settings.
            DefaultLanguage = ("en");
            CookieName = "i18n.langtag";
            MessageKeyIsValueInDefaultLanguage = true;
            PermanentRedirects = false;

            if (string.IsNullOrWhiteSpace(ApplicationPath))
            {
                ApplicationPath = "/";
            }

            // Use default package of root services.
            // Host app may override this.
            RootServices = new DefaultRootServices(environment, redisContext, config);

            // Install default handler for Set-PAL event.
            // The default handler applies the setting to both the CurrentCulture and CurrentUICulture
            // settings of the thread.
            SetPrincipalAppLanguageForRequestHandlers = delegate(ILanguageTag langtag)
            {
                if (langtag != null)
                {
                    Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = langtag.GetCultureInfo();
                }
            };
        }

        /// <summary>
        /// Instance of the this LocalizedApplication class for the current AppDomain.
        /// </summary>
        public static LocalizedApplication Current { get; set; }

        /// <summary>
        /// This object relays its implementaion of IRootServices onto the object set here.
        /// Host app may override with its own implementation.
        /// By default, this property is set to an instance of DefaultRootServices.
        /// </summary>
        public IRootServices RootServices { get; set; }
    }
}
