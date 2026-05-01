using MyLocalAssistant.Plugin.Shared;
using MyLocalAssistant.Plugins.ReportGen;
using QuestPDF.Infrastructure;

// QuestPDF Community License for non-commercial / open-source use.
QuestPDF.Settings.License = LicenseType.Community;

var handler = new ReportGenHandler();
await new PluginHost()
    .Register("report.pdf",   handler)
    .Register("report.word",  handler)
    .Register("report.excel", handler)
    .RunAsync();
