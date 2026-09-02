using AgentBridge.Abstractions.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentBridge.Infrastructure.FileWatching;

public sealed class FileWatcherFactory(FileWatcherOptions options, ILoggerFactory loggerFactory) : IFileWatcherFactory
{
    public IFileWatcher Create(string filePath) =>
        new FileWatcherService(filePath, options, loggerFactory.CreateLogger<FileWatcherService>());
}
