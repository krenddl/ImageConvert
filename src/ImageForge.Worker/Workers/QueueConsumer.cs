using System.Text;
using System.Text.Json;
using ImageForge.Shared.Contracts;
using ImageForge.Shared.Messaging;
using ImageForge.Worker.Services;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImageForge.Worker.Workers;

// Long-running consumer registered as a HostedService. Connects to RabbitMQ,
// subscribes to the task queue and processes messages one at a time per worker.
// On success: ack the message. On failure: nack without requeue (would loop).
public sealed class QueueConsumer : BackgroundService
{
    private readonly ILogger<QueueConsumer> _logger;
    private readonly RabbitMqOptions _options;
    private readonly ImageProcessor _processor;
    private IConnection? _connection;
    private IModel? _channel;

    public QueueConsumer(
        IOptions<RabbitMqOptions> options,
        ImageProcessor processor,
        ILogger<QueueConsumer> logger)
    {
        _options = options.Value;
        _processor = processor;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.User,
            Password = _options.Password,
            // Required so AsyncEventingBasicConsumer fires Received on the
            // async pump rather than the regular thread pool.
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection("imageforge-worker");
        _channel = _connection.CreateModel();

        _channel.QueueDeclare(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        // Fair dispatch: only push the next message when this worker acks the
        // current one. With multiple workers this balances load by capacity.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageAsync;

        _channel.BasicConsume(
            queue: _options.Queue,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("Worker consuming queue {Queue} on {Host}:{Port}",
            _options.Queue, _options.Host, _options.Port);

        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        TaskMessage? message = null;
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            message = JsonSerializer.Deserialize<TaskMessage>(json);

            if (message is null)
            {
                _logger.LogWarning("Received empty or invalid message, dropping.");
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            _logger.LogInformation("Received task {TaskId}, starting processing", message.TaskId);

            await _processor.ProcessAsync(message, CancellationToken.None);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process task {TaskId} (delivery {DeliveryTag})",
                message?.TaskId ?? "<unknown>", ea.DeliveryTag);
            // Do not requeue: malformed inputs or unsupported formats would loop forever.
            // Persistent status with "failed" lands in M5.
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
