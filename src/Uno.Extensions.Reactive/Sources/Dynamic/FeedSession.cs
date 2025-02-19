﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Uno.Extensions.Reactive.Core;
using Uno.Extensions.Reactive.Operators;

namespace Uno.Extensions.Reactive.Sources;

/// <summary>
/// Represents an enumeration session of a <see cref="DynamicFeed{T}"/> for a given <see cref="SourceContext"/>.
/// This is created each time you <see cref="ISignal{T}.GetSource"/> of a DynamicFeed.
/// </summary>
internal abstract class FeedSession : IAsyncDisposable
{
	// Debug: event EventHandler<FeedAsyncExecution>? ExecutionStarted;

	private readonly CancellationTokenSource _ct;
	private readonly CancellationTokenRegistration _ctSub; // Subscription to the parent token
	private int _isDisposed;

	private protected FeedSession(ISignal<IMessage> owningSignal, SourceContext context, CancellationToken ct)
	{
		Owner = owningSignal;
		Context = context;

		_ct = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_ctSub = ct.Register(static session => _ = ((FeedSession)session).DisposeAsync(), this, useSynchronizationContext: false);
	}

	/// <summary>
	/// The context for which this sessions is created.
	/// </summary>
	public SourceContext Context { get; }

	/// <summary>
	/// A cancellation token that is linked to the life time of this session.
	/// </summary>
	/// <remarks>
	/// This token will be cancelled at the end of the current session while the <see cref="SourceContext.Token"/>
	/// is linked to the lifetime of the owning source context and will remain active across sessions!
	/// </remarks>
	public CancellationToken Token => _ct.Token;

	/// <summary>
	/// The signal which created this session.
	/// </summary>
	public ISignal<IMessage> Owner { get; }

	/// <summary>
	/// Indicates if the session has been disposed.
	/// </summary>
	protected bool IsDisposed => _isDisposed is not 0;

	/// <summary>
	/// Requests to start a new execution.
	/// </summary>
	/// <param name="request">The execution request.</param>
	/// <returns></returns>
	public abstract void Execute(ExecuteRequest request);

	#region Dependencies support (including parent feeds dependencies)
	private ImmutableList<IDependency> _dependencies = ImmutableList<IDependency>.Empty;
	private FeedDependenciesStore? _feedDependencies;

	/// <summary>
	/// The feed on which the <see cref="Owner"/> is dependent upon.
	/// </summary>
	/// <remarks>FeedDependency managed by that store are also present in the <see cref="Dependencies"/> collection.</remarks>
	internal FeedDependenciesStore Feeds
	{
		get
		{
			// As lot of feed might not have feeds dependencies, we init store lazily
			if (_feedDependencies is null)
			{
				Interlocked.CompareExchange(ref _feedDependencies, new(this), null);
			}
			return _feedDependencies;
		}
	}

	/// <summary>
	/// Gets the list of the currently registered dependencies.
	/// </summary>
	public IImmutableList<IDependency> Dependencies => _dependencies;

	/// <summary>
	/// Allows the FeedDependenciesStore to notify that the parent message has been updated.
	/// </summary>
	internal abstract void OnParentUpdated();

	/// <summary>
	/// Registers a dependency for the current session.
	/// </summary>
	/// <param name="dependency">A dependency that can trigger a <see cref="Execute"/>.</param>
	public void RegisterDependency(IDependency dependency)
	{
		if (IsDisposed)
		{
			return;
		}

		ImmutableInterlocked.Update(ref _dependencies, static (list, item) => list.Add(item), dependency);
	}

	/// <summary>
	/// Un-registers a dependency from the current session.
	/// </summary>
	/// <param name="dependency">The dependency to remove.</param>
	/// <remarks>Removing the last dependency will cause teh session to complete (or at the end of the current execution).</remarks>
	public void UnRegisterDependency(IDependency dependency)
	{
		if (IsDisposed)
		{
			return;
		}

		if (ImmutableInterlocked.Update(ref _dependencies, static (list, item) => list.Remove(item), dependency) && _dependencies is { Count: 0 })
		{
			TryComplete();
		}
	}
	#endregion

	#region Session lifetime objects (extensions helper, no behavior impact)
	private readonly Dictionary<object, object> _sharedInstances = new();

	/// <summary>
	/// Sets an object that will be shared across all executions of the current session.
	/// </summary>
	/// <typeparam name="TKey">Type of the key</typeparam>
	/// <typeparam name="TValue">Type of the shared object</typeparam>
	/// <param name="key">The key that identifies the value. It has to be unique between all dependencies.</param>
	/// <param name="value">The shared instance.</param>
	public void SetShared<TKey, TValue>(TKey key, TValue value)
		where TKey : notnull
		where TValue : notnull
	{
		lock (_sharedInstances)
		{
			if (IsDisposed)
			{
				throw new ObjectDisposedException(nameof(FeedSession), $"Cannot set shared instance of {typeof(TValue)} on a completed session.");
			}

			_sharedInstances[key] = value;
		}
	}

	/// <summary>
	/// Gets or create an object that will be shared across all executions of the current session.
	/// </summary>
	/// <typeparam name="TKey">Type of the key</typeparam>
	/// <typeparam name="TArgs">Type of the arguments for the <paramref name="factory"/>.</typeparam>
	/// <typeparam name="TValue">Type of the shared object</typeparam>
	/// <param name="key">The key that identifies the value. It has to be unique between all dependencies.</param>
	/// <param name="factory">Factory to create the object if missing.</param>
	/// <param name="args">The arguments for the <paramref name="factory"/>.</param>
	public TValue GetShared<TKey, TArgs, TValue>(TKey key, Func<FeedSession, TKey, TArgs, TValue> factory, TArgs args)
		where TKey : notnull
		where TValue : notnull
	{
		lock (_sharedInstances)
		{
			if (IsDisposed)
			{
				throw new ObjectDisposedException(nameof(FeedSession), $"Cannot get shared instance of {typeof(TValue)} on a completed session.");
			}

			if (!_sharedInstances.TryGetValue(key, out var value))
			{
				_sharedInstances[key] = value = factory(this, key, args);
			}

			return (TValue)value;
		}
	} 
	#endregion

	/// <summary>
	/// Completes the current session (i.e. complete the underlying AsyncEnumerable).
	/// </summary>
	/// <remarks>This is invoked at the end of an execution if there is no pending request AND when we remove the last dependency.</remarks>
	protected abstract void TryComplete();

	/// <inheritdoc />
	public virtual async ValueTask DisposeAsync()
	{
		if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) is 0) // Id double dispose, _ct.Cancel() would crash as it has been disposed.
		{
			_ctSub.Dispose();
			_ct.Cancel();
			_dependencies = ImmutableList<IDependency>.Empty;
			Feeds.Dispose();
			lock (_sharedInstances)
			{
				_sharedInstances.Clear(); // TODO: Dispose instances?
			}

			_ct.Dispose();
		}
	}

	~FeedSession()
	{
		_ = DisposeAsync();
	}
}
