using System;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastSTRM;

/// <summary>
/// Registers plugin services via Jellyfin's DI pipeline.
/// Decorates <see cref="IMediaSourceManager"/> with <see cref="MediaSourceInterceptor"/>,
/// decorates <see cref="IUserDataManager"/> with <see cref="PlaybackStateInterceptor"/>
/// and registers <see cref="ProbeService"/> as a hosted singleton.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register ProbeService as singleton + hosted service for ItemAdded event lifecycle
        serviceCollection.AddSingleton<ProbeService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProbeService>());

        // Decorate IMediaSourceManager to skip ffprobe for .strm playback.
        DecorateService<IMediaSourceManager>(
            serviceCollection,
            (sp, inner) => new MediaSourceInterceptor(
                inner,
                sp.GetRequiredService<ILogger<MediaSourceInterceptor>>(),
                sp));

        // Decorate IUserDataManager so progress reported for an item that is still being
        // probed is not thrown away by Jellyfin's "unknown runtime means fully played" rule.
        DecorateService<IUserDataManager>(
            serviceCollection,
            (sp, inner) => new PlaybackStateInterceptor(
                inner,
                sp,
                sp.GetRequiredService<ILogger<PlaybackStateInterceptor>>()));
    }

    /// <summary>
    /// Replaces a service registration with a decorator that wraps the original implementation.
    /// </summary>
    private static void DecorateService<TService>(
        IServiceCollection serviceCollection,
        Func<IServiceProvider, TService, TService> decoratorFactory)
        where TService : class
    {
        var descriptor = serviceCollection.FirstOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is null)
        {
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.Add(new ServiceDescriptor(
            typeof(TService),
            sp => decoratorFactory(sp, CreateInstance<TService>(sp, descriptor)),
            descriptor.Lifetime));
    }

    private static TService CreateInstance<TService>(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
        where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (TService)descriptor.ImplementationFactory(serviceProvider);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (TService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            $"The original {typeof(TService).FullName} registration cannot be resolved.");
    }
}
