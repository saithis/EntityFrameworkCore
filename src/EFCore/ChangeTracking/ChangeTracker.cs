// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.ChangeTracking
{
    /// <summary>
    ///     Provides access to change tracking information and operations for entity instances the context is tracking.
    ///     Instances of this class are typically obtained from <see cref="DbContext.ChangeTracker" /> and it is not designed
    ///     to be directly constructed in your application code.
    /// </summary>
    public class ChangeTracker : IInfrastructure<IStateManager>
    {
        private readonly IModel _model;
        private QueryTrackingBehavior _queryTrackingBehavior;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public ChangeTracker(
            [NotNull] DbContext context,
            [NotNull] IStateManager stateManager,
            [NotNull] IChangeDetector changeDetector,
            [NotNull] IModel model,
            [NotNull] IEntityEntryGraphIterator graphIterator)
        {
            Check.NotNull(context, nameof(context));
            Check.NotNull(stateManager, nameof(stateManager));
            Check.NotNull(changeDetector, nameof(changeDetector));

            Context = context;

            _queryTrackingBehavior = context
                                         .GetService<IDbContextOptions>()
                                         .Extensions
                                         .OfType<CoreOptionsExtension>()
                                         .FirstOrDefault()
                                         ?.QueryTrackingBehavior
                                     ?? QueryTrackingBehavior.TrackAll;

            stateManager.EntityStateChangingEventBridge = OnEnityStateChanging;
            stateManager.EntityStateChangedEventBridge = OnEnityStateChanged;

            StateManager = stateManager;
            ChangeDetector = changeDetector;
            _model = model;
            GraphIterator = graphIterator;
        }

        /// <summary>
        ///     <para>
        ///         Gets or sets a value indicating whether the <see cref="DetectChanges()" /> method is called
        ///         automatically by methods of <see cref="DbContext" /> and related classes.
        ///     </para>
        ///     <para>
        ///         The default value is true. This ensures the context is aware of any changes to tracked entity instances
        ///         before performing operations such as <see cref="DbContext.SaveChanges()" /> or returning change tracking
        ///         information. If you disable automatic detect changes then you must ensure that
        ///         <see cref="DetectChanges()" /> is called when entity instances have been modified.
        ///         Failure to do so may result in some changes not being persisted during
        ///         <see cref="DbContext.SaveChanges()" /> or out-of-date change tracking information being returned.
        ///     </para>
        /// </summary>
        public virtual bool AutoDetectChangesEnabled { get; set; } = true;

        /// <summary>
        ///     <para>
        ///         Gets or sets a value indicating whether navigation properties for tracked entities
        ///         will be loaded on first access.
        ///     </para>
        ///     <para>
        ///         The default value is true. However, lazy loading will only occur for navigation properties
        ///         of entities that have also been configured in the model for lazy loading.
        ///     </para>
        /// </summary>
        public virtual bool LazyLoadingEnabled { get; set; } = true;

        /// <summary>
        ///     <para>
        ///         Gets or sets the tracking behavior for LINQ queries run against the context. Disabling change tracking
        ///         is useful for read-only scenarios because it avoids the overhead of setting up change tracking for each
        ///         entity instance. You should not disable change tracking if you want to manipulate entity instances and
        ///         persist those changes to the database using <see cref="DbContext.SaveChanges()" />.
        ///     </para>
        ///     <para>
        ///         This method sets the default behavior for the context, but you can override this behavior for individual
        ///         queries using the <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}(IQueryable{TEntity})" />
        ///         and <see cref="EntityFrameworkQueryableExtensions.AsTracking{TEntity}(IQueryable{TEntity})" /> methods.
        ///     </para>
        ///     <para>
        ///         The default value is <see cref="EntityFrameworkCore.QueryTrackingBehavior.TrackAll" />. This means the change tracker will
        ///         keep track of changes for all entities that are returned from a LINQ query.
        ///     </para>
        /// </summary>
        public virtual QueryTrackingBehavior QueryTrackingBehavior
        {
            get => _queryTrackingBehavior;
            set => _queryTrackingBehavior = value;
        }

        /// <summary>
        ///     Gets an <see cref="EntityEntry" /> for each entity being tracked by the context.
        ///     The entries provide access to change tracking information and operations for each entity.
        /// </summary>
        /// <returns> An entry for each entity being tracked. </returns>
        public virtual IEnumerable<EntityEntry> Entries()
        {
            TryDetectChanges();

            return StateManager.Entries.Select(e => new EntityEntry(e));
        }

        /// <summary>
        ///     Gets an <see cref="EntityEntry" /> for all entities of a given type being tracked by the context.
        ///     The entries provide access to change tracking information and operations for each entity.
        /// </summary>
        /// <typeparam name="TEntity"> The type of entities to get entries for. </typeparam>
        /// <returns> An entry for each entity of the given type that is being tracked. </returns>
        public virtual IEnumerable<EntityEntry<TEntity>> Entries<TEntity>()
            where TEntity : class
        {
            TryDetectChanges();

            return StateManager.Entries
                .Where(e => e.Entity is TEntity)
                .Select(e => new EntityEntry<TEntity>(e));
        }

        private void TryDetectChanges()
        {
            if (AutoDetectChangesEnabled)
            {
                DetectChanges();
            }
        }

        /// <summary>
        ///     <para>
        ///         Checks if any new, deleted, or changed entities are being tracked
        ///         such that these changes will be sent to the database if <see cref="DbContext.SaveChanges()" />
        ///         or <see cref="DbContext.SaveChangesAsync(System.Threading.CancellationToken)" /> is called.
        ///     </para>
        ///     <para>
        ///         Note that this method calls <see cref="DetectChanges" /> unless
        ///         <see cref="AutoDetectChangesEnabled" /> has been set to false.
        ///     </para>
        /// </summary>
        /// <returns> True if there are changes to save, otherwise false. </returns>
        public virtual bool HasChanges()
        {
            if (AutoDetectChangesEnabled)
            {
                DetectChanges();
            }

            return StateManager.ChangedCount > 0;
        }

        /// <summary>
        ///     <para>
        ///         Gets the internal state manager being used to store information about tracked entities.
        ///     </para>
        ///     <para>
        ///         This property is intended for use by extension methods. It is not intended to be used in
        ///         application code.
        ///     </para>
        /// </summary>
        [Obsolete]
        IStateManager IInfrastructure<IStateManager>.Instance => StateManager;

        /// <summary>
        ///     Gets the context this change tracker belongs to.
        /// </summary>
        public virtual DbContext Context { get; }

        /// <summary>
        ///     Scans the tracked entity instances to detect any changes made to the instance data. <see cref="DetectChanges()" />
        ///     is usually called automatically by the context when up-to-date information is required (before
        ///     <see cref="DbContext.SaveChanges()" /> and when returning change tracking information). You typically only need to
        ///     call this method if you have disabled <see cref="AutoDetectChangesEnabled" />.
        /// </summary>
        public virtual void DetectChanges()
        {
            if (_model[Internal.ChangeDetector.SkipDetectChangesAnnotation] == null)
            {
                ChangeDetector.DetectChanges(StateManager);
            }
        }

        /// <summary>
        ///     Accepts all changes made to entities in the context. It will be assumed that the tracked entities
        ///     represent the current state of the database. This method is typically called by <see cref="DbContext.SaveChanges()" />
        ///     after changes have been successfully saved to the database.
        /// </summary>
        public virtual void AcceptAllChanges() => StateManager.AcceptAllChanges();

        /// <summary>
        ///     <para>
        ///         Begins tracking an entity and any entities that are reachable by traversing it's navigation properties.
        ///         Traversal is recursive so the navigation properties of any discovered entities will also be scanned.
        ///         The specified <paramref name="callback" /> is called for each discovered entity and must set the
        ///         <see cref="EntityEntry.State" /> that each entity should be tracked in. If no state is set, the entity
        ///         remains untracked.
        ///     </para>
        ///     <para>
        ///         This method is designed for use in disconnected scenarios where entities are retrieved using one instance of
        ///         the context and then changes are saved using a different instance of the context. An example of this is a
        ///         web service where one service call retrieves entities from the database and another service call persists
        ///         any changes to the entities. Each service call uses a new instance of the context that is disposed when the
        ///         call is complete.
        ///     </para>
        ///     <para>
        ///         If an entity is discovered that is already tracked by the context, that entity is not processed (and it's
        ///         navigation properties are not traversed).
        ///     </para>
        /// </summary>
        /// <param name="rootEntity"> The entity to begin traversal from. </param>
        /// <param name="callback">
        ///     An action to configure the change tracking information for each entity. For the entity to begin being tracked,
        ///     the <see cref="EntityEntry.State" /> must be set.
        /// </param>
        public virtual void TrackGraph(
            [NotNull] object rootEntity,
            [NotNull] Action<EntityEntryGraphNode> callback)
        {
            Check.NotNull(rootEntity, nameof(rootEntity));
            Check.NotNull(callback, nameof(callback));

            var rootEntry = StateManager.GetOrCreateEntry(rootEntity);

            GraphIterator.TraverseGraph(
                new EntityEntryGraphNode(rootEntry, null, null),
                n =>
                    {
                        if (n.Entry.State != EntityState.Detached)
                        {
                            return false;
                        }

                        callback(n);

                        return n.Entry.State != EntityState.Detached;
                    });
        }

        private IStateManager StateManager { get; }

        private IChangeDetector ChangeDetector { get; }

        private IEntityEntryGraphIterator GraphIterator { get; }

        /// <summary>
        ///     An event fired when an entity that is tracked by the associated <see cref="DbContext" /> is about
        ///     to move from one <see cref="EntityState" /> to another.
        /// </summary>
        public event Action<object, EntityStateEventArgs> EnityStateChanging;

        private void OnEnityStateChanging(InternalEntityEntry internalEntityEntry, EntityState newState, bool fromQuery)
        {
            var @event = EnityStateChanging;

            @event?.Invoke(this, new EntityStateEventArgs(internalEntityEntry, internalEntityEntry.EntityState, newState, fromQuery));
        }

        /// <summary>
        ///     An event fired when an entity that is tracked by the associated <see cref="DbContext" /> has moved
        ///     from one <see cref="EntityState" /> to another.
        /// </summary>
        public event Action<object, EntityStateEventArgs> EnityStateChanged;

        private void OnEnityStateChanged(InternalEntityEntry internalEntityEntry, EntityState oldState, bool fromQuery)
        {
            var @event = EnityStateChanged;

            @event?.Invoke(this, new EntityStateEventArgs(internalEntityEntry, oldState, internalEntityEntry.EntityState, fromQuery));
        }

        #region Hidden System.Object members

        /// <summary>
        ///     Returns a string that represents the current object.
        /// </summary>
        /// <returns> A string that represents the current object. </returns>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override string ToString() => base.ToString();

        /// <summary>
        ///     Determines whether the specified object is equal to the current object.
        /// </summary>
        /// <param name="obj"> The object to compare with the current object. </param>
        /// <returns> true if the specified object is equal to the current object; otherwise, false. </returns>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool Equals(object obj) => base.Equals(obj);

        /// <summary>
        ///     Serves as the default hash function.
        /// </summary>
        /// <returns> A hash code for the current object. </returns>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override int GetHashCode() => base.GetHashCode();

        #endregion
    }
}
