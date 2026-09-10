using UnityEngine;

namespace AgenticRacing.Strategy
{
    /// <summary>
    /// Resolves the strategist endpoint. The demo is served from the site root
    /// by the same FastAPI process that proxies the LLM (CLAUDE.md §2.2, §3), so
    /// in a WebGL build the endpoint is just <c>&lt;page-origin&gt;/api/strategy</c>
    /// — relative paths only, no hostnames baked in. In the Editor / a
    /// standalone player it defaults to a local dev server, overridable with
    /// <see cref="Override"/> or the <c>AGENTIC_API_BASE</c> env var.
    /// </summary>
    public static class StrategyApi
    {
        private const string Path = "api/strategy";
        private const string EditorDefaultBase = "http://localhost:8080";

        /// <summary>Set by a bootstrap to point at a non-default server. Ignored
        /// in WebGL, where the origin is always the page's own.</summary>
        public static string Override;

        public static string Url() => Combine(BaseUrl(), Path);

        public static string PingUrl() => Combine(BaseUrl(), "api/ping");

        private static string BaseUrl()
        {
            if (!string.IsNullOrEmpty(Override)) return Override;

#if UNITY_WEBGL && !UNITY_EDITOR
            // Application.absoluteURL: the full page URL. Take scheme://host[:port].
            string page = Application.absoluteURL;
            if (!string.IsNullOrEmpty(page))
            {
                int schemeEnd = page.IndexOf("://", System.StringComparison.Ordinal);
                if (schemeEnd > 0)
                {
                    int slash = page.IndexOf('/', schemeEnd + 3);
                    return slash > 0 ? page.Substring(0, slash) : page;
                }
            }
            return "";
#else
            string env = System.Environment.GetEnvironmentVariable("AGENTIC_API_BASE");
            return string.IsNullOrEmpty(env) ? EditorDefaultBase : env;
#endif
        }

        private static string Combine(string baseUrl, string path)
        {
            if (string.IsNullOrEmpty(baseUrl)) return path;
            return baseUrl.TrimEnd('/') + "/" + path;
        }
    }
}
