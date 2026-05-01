using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.WebSearch;

var handler = new WebSearchHandler();
await new PluginHost()
    .Register("web.search", handler)
    .Register("web.visit",  handler)
    .RunAsync();
