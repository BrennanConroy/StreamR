using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

public class StreamManager
{
    private readonly ConcurrentDictionary<string, StreamHolder> _streams;
    private long _globalClientId;

    public StreamManager()
    {
        _streams = new ConcurrentDictionary<string, StreamHolder>();
    }

    public List<string> ListStreams()
    {
        var streamList = new List<string>();
        foreach (var item in _streams)
        {
            streamList.Add(item.Key);
        }
        return streamList;
    }

    public async Task RunStreamAsync(string streamName, ChannelReader<string> stream)
    {
        var streamHolder = new StreamHolder() { Source = stream };

        // Add before yielding
        // This fixes a race where we tell clients a new stream arrives before adding the stream
        _streams.TryAdd(streamName, streamHolder);

        await Task.Yield();

        try
        {
            while (await stream.WaitToReadAsync())
            {
                while (stream.TryRead(out var item))
                {
                    foreach (var viewer in streamHolder.Viewers)
                    {
                        try
                        {
                            await viewer.Value.Writer.WriteAsync(item);
                        }
                        catch { }
                    }
                }
            }
        }
        finally
        {
            RemoveStream(streamName);
        }
    }

    public void RemoveStream(string streamName)
    {
        _streams.TryRemove(streamName, out var streamHolder);
        foreach (var viewer in streamHolder.Viewers)
        {
            viewer.Value.Writer.TryComplete();
        }
    }

    public ChannelReader<string> Subscribe(string streamName, CancellationToken token)
    {
        if (!_streams.TryGetValue(streamName, out var source))
        {
            throw new HubException("stream doesn't exist");
        }

        var id = Interlocked.Increment(ref _globalClientId);

        var channel = Channel.CreateBounded<string>(options: new BoundedChannelOptions(2) {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        source.Viewers.TryAdd(id, channel);

        // Register for client closing stream, this token will always fire (handled by SignalR)
        token.Register(() =>
        {
            source.Viewers.TryRemove(id, out _);
        });

        return channel.Reader;
    }

    private class StreamHolder
    {
        public ChannelReader<string> Source;
        public ConcurrentDictionary<long, Channel<string>> Viewers = new ConcurrentDictionary<long, Channel<string>>();
    }
}