using i18n.Domain.Abstract;

namespace i18n
{
    /// <summary>
    /// Describes the set of i18n localizing services registered for the current AppDomain.
    /// Typically these are used by objects at the root of the dependency-injection graph,
    /// such as an HttpModule.
    /// </summary>
    public interface IRootServices
    {
        ITranslationRepository TranslationRepositoryForApp { get; }
        ITextLocalizer TextLocalizerForApp { get; }
        INuggetLocalizer NuggetLocalizerForApp { get; }
    }
}
