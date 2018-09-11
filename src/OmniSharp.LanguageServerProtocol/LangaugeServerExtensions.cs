using System.Collections.Generic;
using System.Linq;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Server;

namespace OmniSharp.LanguageServerProtocol
{
    public static class LangaugeServerExtensions
    {
        public static ILanguageServer AddHandlers(this ILanguageServer langaugeServer, IEnumerable<IJsonRpcHandler> handlers)
        {
            if (handlers != null)
            {
                langaugeServer.AddHandlers(handlers.ToArray());
            }

            return langaugeServer;
        }
    }
}
