using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.ImageGen;

await new PluginHost()
    .Register("image.generate", new ImageGenHandler())
    .RunAsync();
