using Microsoft.VisualStudio.Shell;

namespace VsBridge
{
    public class VsBridgeOptions : DialogPage
    {
        public string BridgeUrl { get; set; } = "http://127.0.0.1:5005/v1/notify";
        public string Token { get; set; } = "dev-token";
    }
}
