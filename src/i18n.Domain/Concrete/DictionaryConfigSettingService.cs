using System.Collections.Generic;
using i18n.Domain.Abstract;

namespace i18n.Domain.Concrete
{
    public class DictionaryConfigSettingService : AbstractSettingService
    {
        private readonly IDictionary<string, string> config;

        public DictionaryConfigSettingService(IDictionary<string, string> config = null) : base(null)
        {
            this.config = config ?? new Dictionary<string, string>();
        }

        public override string GetConfigFileLocation() => null;

        public override string GetSetting(string key)
        {
            if (config.ContainsKey(key))
            {
                return config[key];
            }

            return null;
        }

        public override void SetSetting(string key, string value)
        {
            config[key] = value;
        }

        public override void RemoveSetting(string key)
        {
            config.Remove(key);
        }
    }
}
