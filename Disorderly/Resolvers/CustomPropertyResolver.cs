using System.Linq.Expressions;
using ReactiveUI;
using ReactiveUI.Primitives;
using Observable = System.Reactive.Linq.Observable;

namespace Disorderly.Resolvers;

/// <summary>
/// Resolves observables for custom properties.
/// </summary>
public class CustomPropertyResolver : ICreatesObservableForProperty
{
    /// <inheritdoc />
    public int GetAffinityForObject(Type? type, string propertyName, bool beforeChanged = false) => 1;

    /// <inheritdoc />
    public int GetAffinityForObject(Type type, string propertyName) => 1;

    /// <inheritdoc />
    public IObservable<IObservedChange<object, object>> GetNotificationForProperty(object sender, Expression expression, string propertyName, bool beforeChanged = false, bool suppressWarnings = false)
    {
        return Observable.Return(new ObservedChange<object, object>(sender, expression, default!))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Concat(Observable.Never<IObservedChange<object, object>>());
    }

    /// <inheritdoc />
    public IObservable<IObservedChange<object?, object?>> GetNotificationForProperty(object sender, Expression expression, string propertyName)
    {
        return Observable.Return(new ObservedChange<object, object>(sender, expression, default!))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Concat(Observable.Never<IObservedChange<object, object>>());
    }

    /// <inheritdoc />
    public IObservable<IObservedChange<object?, object?>> GetNotificationForProperty(object sender, Expression expression, string propertyName, bool beforeChanged)
    {
        return Observable.Return(new ObservedChange<object, object>(sender, expression, default!))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Concat(Observable.Never<IObservedChange<object, object>>());
    }
}