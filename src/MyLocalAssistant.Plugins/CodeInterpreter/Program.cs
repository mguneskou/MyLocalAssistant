using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.CodeInterpreter;

await new PluginHost()
    .Register("code.run", new CodeInterpreterHandler())
    .RunAsync();
