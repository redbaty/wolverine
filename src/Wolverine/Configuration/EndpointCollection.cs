using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.ImTools;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Configuration;

public class EndpointCollection : IAsyncDisposable
{
    private readonly WolverineRuntime _runtime;

    internal EndpointCollection(WolverineRuntime runtime)
    {
        _runtime = runtime;
        _options = runtime.Options;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _senders.Enumerate())
        {
            var sender = kv.Value;
            if (sender is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (sender is IDisposable d)
            {
                d.Dispose();
            }
        }

        foreach (var value in _listeners.Values)
        {
            await value.DisposeAsync();
        }
    }

    private readonly object _channelLock = new();

    private ImHashMap<string, ISendingAgent> _localSenders = ImHashMap<string, ISendingAgent>.Empty;

    private ImHashMap<Uri, ISendingAgent> _senders = ImHashMap<Uri, ISendingAgent>.Empty!;

    public ISendingAgent CreateSendingAgent(Uri? replyUri, ISender sender, Endpoint endpoint)
    {
        try
        {
            endpoint.Compile(_options);
            var agent = buildSendingAgent(sender, endpoint);
            endpoint.Agent = agent;

            agent.ReplyUri = replyUri;

            endpoint.Agent = agent;

            if (sender is ISenderRequiresCallback senderRequiringCallback && agent is ISenderCallback callbackAgent)
            {
                senderRequiringCallback.RegisterCallback(callbackAgent);
            }

            return agent;
        }
        catch (Exception e)
        {
            throw new TransportEndpointException(sender.Destination,
                "Could not build sending sendingAgent. See inner exception.", e);
        }
    }

    public IEnumerable<IListeningAgent> ActiveListeners()
    {
        return _listeners.Values;
    }

    public ISendingAgent GetOrBuildSendingAgent(Uri address, Action<Endpoint>? configureNewEndpoint = null)
    {
        if (address == null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        if (_senders.TryFind(address, out var agent))
        {
            return agent;
        }

        lock (_channelLock)
        {
            if (_senders.TryFind(address, out agent))
            {
                return agent;
            }

            agent = buildSendingAgent(address, configureNewEndpoint);
            _senders = _senders.AddOrUpdate(address, agent);

            return agent;
        }
    }

    private readonly Dictionary<Uri, ListeningAgent> _listeners = new();
    private readonly WolverineOptions _options;

    public Endpoint? EndpointFor(Uri uri)
    {
        var endpoint = _options.Transports.SelectMany(x => x.Endpoints()).FirstOrDefault(x => x.Uri == uri);
        endpoint?.Compile(_options);

        return endpoint;
    }

    private ISendingAgent buildSendingAgent(ISender sender, Endpoint endpoint)
    {
        // This is for the stub transport in the Storyteller specs
        if (sender is ISendingAgent a)
        {
            return a;
        }

        switch (endpoint.Mode)
        {
            case EndpointMode.Durable:
                return new DurableSendingAgent(sender, _options.Advanced, _runtime.Logger, _runtime.MessageLogger,
                    _runtime.Persistence, endpoint);

            case EndpointMode.BufferedInMemory:
                return new BufferedSendingAgent(_runtime.Logger, _runtime.MessageLogger, sender, _runtime.Advanced, endpoint);

            case EndpointMode.Inline:
                return new InlineSendingAgent(sender, endpoint, _runtime.MessageLogger, _runtime.Advanced);
        }

        throw new InvalidOperationException();
    }

    public ISendingAgent AgentForLocalQueue(string queueName)
    {
        if (_localSenders.TryFind(queueName, out var agent))
        {
            return agent;
        }

        agent = GetOrBuildSendingAgent($"local://{queueName}".ToUri());
        _localSenders = _localSenders.AddOrUpdate(queueName, agent);

        return agent;
    }

    private ISendingAgent buildSendingAgent(Uri uri, Action<Endpoint>? configureNewEndpoint)
    {
        var transport = _options.Transports.ForScheme(uri.Scheme);
        if (transport == null)
        {
            throw new InvalidOperationException(
                $"There is no known transport type that can send to the Destination {uri}");
        }

        var endpoint = transport.GetOrCreateEndpoint(uri);
        configureNewEndpoint?.Invoke(endpoint);
        
        endpoint.Compile(_options);

        endpoint.Runtime ??= _runtime; // This is important for serialization
        return endpoint.StartSending(_runtime, transport.ReplyEndpoint()?.Uri);
    }

    public Endpoint? EndpointByName(string endpointName)
    {
        return _options.Transports.AllEndpoints().ToArray().FirstOrDefault(x => x.Name == endpointName);
    }

    public IListeningAgent? FindListeningAgent(Uri uri)
    {
        if (_listeners.TryGetValue(uri, out var agent))
        {
            return agent;
        }

        return null;
    }

    public IListeningAgent? FindListeningAgent(string endpointName)
    {
        return _listeners.Values.FirstOrDefault(x => x.Endpoint.Name.EqualsIgnoreCase(endpointName));
    }

    internal async Task StartListeners()
    {
        var listeningEndpoints = _options.Transports.SelectMany(x => x.Endpoints())
            .Where(x => x.IsListener).Where(x => x is not LocalQueueSettings);

        foreach (var endpoint in listeningEndpoints)
        {
            endpoint.Compile(_options);
            var agent = new ListeningAgent(endpoint, _runtime);
            await agent.StartAsync().ConfigureAwait(false);
            _listeners[agent.Uri] = agent;
        }
    }
}