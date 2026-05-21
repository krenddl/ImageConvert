using ImageForge.Shared.Messaging;
using ImageForge.Worker.Services;
using ImageForge.Worker.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddSingleton<WorkerStorage>();
builder.Services.AddSingleton<ImageProcessor>();
builder.Services.AddHostedService<QueueConsumer>();

var host = builder.Build();
host.Run();
