using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.MemoryTool;

var handler = new MemoryToolHandler();
await new PluginHost()
    .Register("memory.save",   handler)
    .Register("memory.recall", handler)
    .Register("memory.list",   handler)
    .Register("memory.delete", handler)
    .Register("memory.search", handler)
    .RunAsync();
