using System;
using System.Composition.Hosting;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using OmniSharp.LanguageServerProtocol.Eventing;
using OmniSharp.LanguageServerProtocol.Handlers;
using OmniSharp.Mef;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Services;
using OmniSharp.Utilities;

namespace OmniSharp.LanguageServerProtocol
{
    public class LanguageServerHost : IDisposable
    {
        private readonly ServiceCollection _services;

        private LanguageServer _server;
        private CompositionHost _compositionHost;
        private readonly LanguageServerLoggerFactory _loggerFactory;
        private readonly CommandLineApplication _application;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private IServiceProvider _serviceProvider;
        private RequestHandlers _handlers;
        private OmniSharpEnvironment _environment;
        private ILogger<LanguageServerHost> _logger;

        private Stream _input;
        private Stream _output;

        public LanguageServerHost(
            Stream input,
            Stream output,
            CommandLineApplication application,
            CancellationTokenSource cancellationTokenSource)
        {
            _input = input;
            _output = output;

            _services = new ServiceCollection();
            _loggerFactory = new LanguageServerLoggerFactory();
            _services.AddSingleton<ILoggerFactory>(_loggerFactory);

            _application = application;
            _cancellationTokenSource = cancellationTokenSource;
        }

        private LanguageServer Server
        {
            get
            {
                if (_server == null)
                {
                    _server = LanguageServer.From(options =>
                        options
                            .WithInput(_input)
                            .WithOutput(_output)
                            .WithLoggerFactory(_loggerFactory)
                            .OnInitialize(Initialize)
                            .InitializeNow(false)
                    ).Result;
                }

                return _server;
            }
        }

        public void Dispose()
        {
            _compositionHost?.Dispose();
            _loggerFactory?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        private static LogLevel GetLogLevel(InitializeTrace initializeTrace)
        {
            switch (initializeTrace)
            {
                case InitializeTrace.Verbose:
                    return LogLevel.Trace;

                case InitializeTrace.Off:
                    return LogLevel.Warning;

                case InitializeTrace.Messages:
                default:
                    return LogLevel.Information;
            }
        }

        private void CreateCompositionHost(InitializeParams initializeParams)
        {   
            _environment = new OmniSharpEnvironment(
                Helpers.FromUri(initializeParams.RootUri),
                Convert.ToInt32(initializeParams.ProcessId ?? -1L),
                GetLogLevel(initializeParams.Trace),
                _application?.OtherArgs?.ToArray()
                );

            // TODO: Make this work with logger factory differently
            // Maybe create a child logger factory?
            _loggerFactory.AddProvider(Server, _environment);
            _logger = _loggerFactory.CreateLogger<LanguageServerHost>();

            var configurationRoot = new ConfigurationBuilder(_environment).Build();
            var eventEmitter = new LanguageServerEventEmitter(Server);
            _serviceProvider = CompositionHostBuilder.CreateDefaultServiceProvider(_environment, configurationRoot, eventEmitter, _services);

            var plugins = _application.CreatePluginAssemblies();
            
            var assemblyLoader = _serviceProvider.GetRequiredService<IAssemblyLoader>();
            var compositionHostBuilder = new CompositionHostBuilder(_serviceProvider)
                .WithOmniSharpAssemblies()
                .WithAssemblies(typeof(LanguageServerHost).Assembly)
                .WithAssemblies(assemblyLoader.LoadByAssemblyNameOrPath(plugins.AssemblyNames).ToArray());

            _compositionHost = compositionHostBuilder.Build();

            var projectSystems = _compositionHost.GetExports<IProjectSystem>();

            var documentSelectors = projectSystems
                .GroupBy(x => x.Language)
                .Select(x => (
                    language: x.Key,
                    selector: new DocumentSelector(x
                        .SelectMany(z => z.Extensions)
                        .Distinct()
                        .Select(z => new DocumentFilter()
                        {
                            Pattern = $"**/*{z}"
                        }))
                    ));

            _logger.LogTrace(
                "Configured Document Selectors {@DocumentSelectors}",
                documentSelectors.Select(x => new { x.language, x.selector })
            );

            // TODO: Get these with metadata so we can attach languages
            // This will thne let us build up a better document filter, and add handles foreach type of handler
            // This will mean that we will have a strategy to create handlers from the interface type
            _handlers = new RequestHandlers(
                _compositionHost.GetExports<Lazy<IRequestHandler, OmniSharpRequestHandlerMetadata>>(),
                documentSelectors
            );

            _logger.LogTrace("--- Handler Definitions ---");
            foreach (var handlerCollection in _handlers)
            {
                foreach (var handler in handlerCollection)
                {
                    _logger.LogTrace(
                        "Handler: {Language}:{DocumentSelector}:{Handler}",
                        handlerCollection.Language,
                        handlerCollection.DocumentSelector.ToString(),
                        handler.GetType().FullName
                    );
                }
            }
            _logger.LogTrace("--- Handler Definitions ---");
        }

        private Task Initialize(InitializeParams initializeParams)
        {
            try
            {
                CreateCompositionHost(initializeParams);

                // TODO: Make it easier to resolve handlers from MEF (without having to add more attributes to the services if we can help it)
                var workspace = _compositionHost.GetExport<OmniSharpWorkspace>();

                Server.AddHandlers(TextDocumentSyncHandler.Enumerate(_handlers, workspace));
                Server.AddHandlers(DefinitionHandler.Enumerate(_handlers));
                Server.AddHandlers(HoverHandler.Enumerate(_handlers));
                Server.AddHandlers(CompletionHandler.Enumerate(_handlers));
                Server.AddHandlers(SignatureHelpHandler.Enumerate(_handlers));
                Server.AddHandlers(RenameHandler.Enumerate(_handlers));
                Server.AddHandlers(DocumentSymbolHandler.Enumerate(_handlers));

                Server.Window?.LogMessage(new LogMessageParams() {
                    Message = "Added handlers... waiting for initialize...",
                    Type = MessageType.Log
                });
            }
            catch (Exception)
            {
                throw;
            }

            return Task.CompletedTask;
        }

        public async Task Start()
        {
            Server.Window?.LogMessage(new LogMessageParams()
            {
                Message = "Starting server...",
                Type = MessageType.Log
            });

            await Server.Initialize();

            Server.Window?.LogMessage(new LogMessageParams()
            {
                Message = "initialized...",
                Type = MessageType.Log
            });

            var logger = _loggerFactory.CreateLogger(typeof(LanguageServerHost));
            WorkspaceInitializer.Initialize(_serviceProvider, _compositionHost);

            // Kick on diagnostics
            var diagnosticHandler = _handlers.GetAll()
                .OfType<IRequestHandler<DiagnosticsRequest, DiagnosticsResponse>>();

            foreach (var handler in diagnosticHandler)
                await handler.Handle(new DiagnosticsRequest());

            logger.LogInformation($"Omnisharp server running using Lsp at location '{_environment.TargetDirectory}' on host {_environment.HostProcessId}.");

            Console.CancelKeyPress += (sender, e) =>
            {
                _cancellationTokenSource.Cancel();
                e.Cancel = true;
            };

            if (_environment.HostProcessId != -1)
            {
                try
                {
                    var hostProcess = Process.GetProcessById(_environment.HostProcessId);
                    hostProcess.EnableRaisingEvents = true;
                    hostProcess.OnExit(() => _cancellationTokenSource.Cancel());
                }
                catch
                {
                    // If the process dies before we get here then request shutdown
                    // immediately
                    _cancellationTokenSource.Cancel();
                }
            }
        }
    }
}
