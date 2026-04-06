namespace ClaudeCode.Cli;

using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

/// <summary>
/// Bridges <see cref="IServiceCollection"/> with Spectre.Console.Cli's
/// <see cref="ITypeRegistrar"/> contract, allowing the CLI framework to
/// resolve commands and their dependencies from the DI container.
/// </summary>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    /// <inheritdoc/>
    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());

    /// <inheritdoc/>
    public void Register(Type service, Type implementation)
        => services.AddSingleton(service, implementation);

    /// <inheritdoc/>
    public void RegisterInstance(Type service, object implementation)
        => services.AddSingleton(service, implementation);

    /// <inheritdoc/>
    public void RegisterLazy(Type service, Func<object> factory)
        => services.AddSingleton(service, _ => factory());
}

/// <summary>
/// Resolves types from an <see cref="IServiceProvider"/> on behalf of
/// Spectre.Console.Cli. Disposes the underlying provider when itself disposed.
/// </summary>
public sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    /// <inheritdoc/>
    public object? Resolve(Type? type)
    {
        if (type is null) return null;
        var service = provider.GetService(type);
        if (service is not null) return service;
        return ActivatorUtilities.CreateInstance(provider, type);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (provider is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (provider is IDisposable disposable)
            disposable.Dispose();
    }
}
