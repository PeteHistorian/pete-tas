using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

// The connection between the players' games (Unity Transport over UDP): one host, any number of others.
// Messages are plain byte arrays; "reliable" ones always arrive and in order, the others are for things
// that are sent again a moment later anyway (where everyone is).
public class NetTransport : IDisposable
{
    public bool IsHost { get; private set; }
    public bool Running { get { return driver.IsCreated; } }
    public string Problem = "";

    // Host: a new player connected / left (their connection number); a client: connected to / lost the host (0)
    public event Action<int> Connected;
    public event Action<int> Disconnected;
    public event Action<int, byte[]> Received;

    private NetworkDriver driver;
    private NetworkPipeline reliable;
    private NetworkPipeline unreliable;
    private NativeArray<byte> scratch;
    private readonly Dictionary<int, NetworkConnection> connections = new Dictionary<int, NetworkConnection>();
    private readonly Dictionary<NetworkConnection, int> numbers = new Dictionary<NetworkConnection, int>();
    private readonly List<KeyValuePair<int, byte[]>> waiting = new List<KeyValuePair<int, byte[]>>();
    private int nextNumber = 1;
    private NetworkConnection server;
    private bool clientConnected;

    private const int MaxMessage = 16000;

    private void Create()
    {
        NetworkSettings settings = new NetworkSettings(Allocator.Temp);
        settings.WithNetworkConfigParameters(connectTimeoutMS: 1000, maxConnectAttempts: 15, disconnectTimeoutMS: 10000, heartbeatTimeoutMS: 500);
        settings.WithReliableStageParameters(windowSize: 64);
        settings.WithFragmentationStageParameters(payloadCapacity: MaxMessage);
        driver = NetworkDriver.Create(settings);
        settings.Dispose();
        reliable = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
        unreliable = driver.CreatePipeline(typeof(UnreliableSequencedPipelineStage));
        scratch = new NativeArray<byte>(MaxMessage, Allocator.Persistent);
    }

    public bool StartHost(ushort port)
    {
        IsHost = true;
        Create();
        NetworkEndPoint endpoint = NetworkEndPoint.AnyIpv4;
        endpoint.Port = port;
        if(driver.Bind(endpoint) != 0)
        {
            Problem = "Couldn't use port " + port + " (is another game already hosting on it?)";
            Dispose();
            return false;
        }
        if(driver.Listen() != 0)
        {
            Problem = "Couldn't start listening on port " + port;
            Dispose();
            return false;
        }
        return true;
    }

    public bool StartClient(string address, ushort port)
    {
        IsHost = false;
        string ip = Resolve(address);
        if(ip == null)
        {
            Problem = "Couldn't find \"" + address + "\". Check the address.";
            return false;
        }
        NetworkEndPoint endpoint;
        if(!NetworkEndPoint.TryParse(ip, port, out endpoint))
        {
            Problem = "\"" + address + "\" isn't a valid address.";
            return false;
        }
        Create();
        server = driver.Connect(endpoint);
        return true;
    }

    // Turns a name like "something.playit.gg" into an IP address; IP addresses are passed through
    private static string Resolve(string address)
    {
        IPAddress parsed;
        if(IPAddress.TryParse(address, out parsed))
        {
            return parsed.ToString();
        }
        try
        {
            foreach(IPAddress a in Dns.GetHostAddresses(address))
            {
                if(a.AddressFamily == AddressFamily.InterNetwork)
                {
                    return a.ToString();
                }
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[Online] " + ex.Message);
        }
        return null;
    }

    public void Update()
    {
        if(!driver.IsCreated)
        {
            return;
        }
        driver.ScheduleUpdate().Complete();
        if(IsHost)
        {
            NetworkConnection c;
            while((c = driver.Accept()) != default(NetworkConnection))
            {
                int n = nextNumber++;
                connections[n] = c;
                numbers[c] = n;
                if(Connected != null) Connected(n);
            }
            foreach(KeyValuePair<int, NetworkConnection> kv in new List<KeyValuePair<int, NetworkConnection>>(connections))
            {
                Pop(kv.Value, kv.Key);
            }
        }
        else if(server.IsCreated)
        {
            Pop(server, 0);
        }
        // Reliable messages that didn't fit in the send window last time
        if(waiting.Count > 0)
        {
            List<KeyValuePair<int, byte[]>> again = new List<KeyValuePair<int, byte[]>>(waiting);
            waiting.Clear();
            foreach(KeyValuePair<int, byte[]> w in again)
            {
                Send(w.Key, w.Value, true);
            }
        }
    }

    private void Pop(NetworkConnection c, int number)
    {
        DataStreamReader reader;
        NetworkEvent.Type type;
        while(driver.IsCreated && (type = driver.PopEventForConnection(c, out reader)) != NetworkEvent.Type.Empty)
        {
            if(type == NetworkEvent.Type.Connect)
            {
                clientConnected = true;
                if(Connected != null) Connected(number);
            }
            else if(type == NetworkEvent.Type.Data)
            {
                int length = reader.Length;
                if(length <= 0 || length > MaxMessage)
                {
                    continue;
                }
                NativeArray<byte> part = scratch.GetSubArray(0, length);
                reader.ReadBytes(part);
                byte[] data = new byte[length];
                NativeArray<byte>.Copy(part, data, length);
                if(Received != null) Received(number, data);
            }
            else if(type == NetworkEvent.Type.Disconnect)
            {
                if(IsHost)
                {
                    connections.Remove(number);
                    numbers.Remove(c);
                }
                else
                {
                    server = default(NetworkConnection);
                    if(!clientConnected)
                    {
                        Problem = "Couldn't reach the host. Check the address, and that the host's port is open.";
                    }
                    clientConnected = false;
                }
                if(Disconnected != null) Disconnected(number);
                return;
            }
        }
    }

    public bool IsConnected
    {
        get { return IsHost ? driver.IsCreated : clientConnected; }
    }

    // Host: to one player (their number); client: to the host (0)
    public void Send(int number, byte[] data, bool isReliable)
    {
        if(!driver.IsCreated || data.Length > MaxMessage)
        {
            return;
        }
        NetworkConnection c;
        if(IsHost)
        {
            if(!connections.TryGetValue(number, out c)) return;
        }
        else
        {
            if(!clientConnected) return;
            c = server;
        }
        DataStreamWriter writer;
        int status = driver.BeginSend(isReliable ? reliable : unreliable, c, out writer, data.Length);
        if(status != 0)
        {
            if(isReliable) waiting.Add(new KeyValuePair<int, byte[]>(number, data));
            return;
        }
        NativeArray<byte> part = scratch.GetSubArray(0, data.Length);
        NativeArray<byte>.Copy(data, part, data.Length);
        writer.WriteBytes(part);
        if(driver.EndSend(writer) < 0 && isReliable)
        {
            waiting.Add(new KeyValuePair<int, byte[]>(number, data));
        }
    }

    // Host only: to every player (except one, if given)
    public void SendAll(byte[] data, bool isReliable, int except = -1)
    {
        foreach(int n in new List<int>(connections.Keys))
        {
            if(n != except) Send(n, data, isReliable);
        }
    }

    public void Kick(int number)
    {
        NetworkConnection c;
        if(IsHost && connections.TryGetValue(number, out c))
        {
            driver.Disconnect(c);
            connections.Remove(number);
            numbers.Remove(c);
        }
    }

    public void Dispose()
    {
        if(driver.IsCreated)
        {
            if(IsHost)
            {
                foreach(NetworkConnection c in connections.Values) driver.Disconnect(c);
            }
            else if(server.IsCreated)
            {
                driver.Disconnect(server);
            }
            driver.ScheduleUpdate().Complete();
            driver.Dispose();
        }
        if(scratch.IsCreated)
        {
            scratch.Dispose();
        }
        connections.Clear();
        numbers.Clear();
        clientConnected = false;
    }
}
