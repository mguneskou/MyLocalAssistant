using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.EmailTool;

await new PluginHost()
    .Register("email.send", new EmailToolHandler())
    .RunAsync();
