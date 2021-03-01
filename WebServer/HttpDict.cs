using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gosub.Web
{
    /// <summary>
    /// Quickie class to make it easier to use string dictionary.  No exceptions
    /// are thrown, and it is possible to supply default values that allow
    /// conversion to long.  
    /// </summary>
    public class HttpDict : Dictionary<string, string>
    {
        /// <summary>
        /// Return the value for key or "" if not found (no exception thrown)
        /// </summary>
        public new string this[string key]
        {
            set { base[key] = value; }
            get
            {
                if (!TryGetValue(key, out string value))
                    value = "";
                return value;
            }
        }

        /// <summary>
        /// Return the value for key or the defaultValue if not found
        /// </summary>
        public string Get(string key, string defaultValue = "")
        {
            if (!TryGetValue(key, out string value))
                value = defaultValue;
            return value;
        }

        /// <summary>
        /// Return the value for key or the defaultValue if not found or not convertable to long
        /// </summary>
        public long Get(string key, long defaultValue)
        {
            if (!TryGetValue(key, out string stringValue))
                return defaultValue;
            if (!long.TryParse(stringValue, out long longValue))
                return defaultValue;
            return longValue;
        }
    }
}
