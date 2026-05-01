using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.Scheduler;

var handler = new SchedulerHandler();
await new PluginHost()
    .Register("schedule.create",   handler)
    .Register("schedule.list",     handler)
    .Register("schedule.cancel",   handler)
    .Register("schedule.run_now",  handler)
    .RunAsync();
