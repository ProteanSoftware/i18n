namespace i18n
{
    /// <summary>
    /// ITranslateSvc implementation that simply passes through the entity (useful for testing).
    /// </summary>
    public class TranslateSvc_Invariant : ITranslateSvc
    {

    #region ITranslateSvc

        public string ParseAndTranslate(string entity)
        {
            return entity;
        }

    #endregion

    }
}
