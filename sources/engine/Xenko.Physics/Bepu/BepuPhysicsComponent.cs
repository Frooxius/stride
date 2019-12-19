// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using BepuPhysics;
using BepuPhysics.Constraints;
using Xenko.Core;
using Xenko.Core.Annotations;
using Xenko.Core.Collections;
using Xenko.Core.Diagnostics;
using Xenko.Core.Mathematics;
using Xenko.Core.MicroThreading;
using Xenko.Engine.Design;
using Xenko.Physics;
using Xenko.Physics.Bepu;
using Xenko.Physics.Engine;
using Xenko.Rendering;

namespace Xenko.Engine
{
    [DataContract("BepuPhysicsComponent", Inherited = true)]
    [Display("BepuPhysics", Expand = ExpandRule.Once)]
    [AllowMultipleComponents]
    [ComponentOrder(3100)]
    //[ComponentCategory("BepuPhysics")]
    public abstract class BepuPhysicsComponent : ActivableEntityComponent
    {
        protected static Logger logger = GlobalLogger.GetLogger("BepuPhysicsComponent");

        internal int AddedHandle = -1;

        public int GetHandle()
        {
            return AddedHandle;
        }

        /// <summary>
        /// Allow BepuHelpers.AddAllBodiesToSimulation to add this?
        /// </summary>
        [DataMember]
        public bool AllowHelperToAdd { get; set; } = true;

        [DataMemberIgnore]
        public virtual bool AddedToScene { get; set; }

        public BepuPhysicsComponent()
        {
            BepuHelpers.AssureServiceAdded();
        }

        /// <summary>
        /// Gets or sets the collision group.
        /// </summary>
        /// <value>
        /// The collision group.
        /// </value>
        /// <userdoc>
        /// Which collision group the component belongs to. This can't be changed at runtime. The default is DefaultFilter. 
        /// </userdoc>
        /// <remarks>
        /// The collider will still produce events, to allow non trigger rigidbodies or static colliders to act as a trigger if required for certain filtering groups.
        /// </remarks>
        [DataMember(30)]
        [Display("Collision group")]
        [DefaultValue(CollisionFilterGroups.DefaultFilter)]
        public CollisionFilterGroups CollisionGroup { get; set; } = CollisionFilterGroups.DefaultFilter;

        /// <summary>
        /// Gets or sets the can collide with.
        /// </summary>
        /// <value>
        /// The can collide with.
        /// </value>
        /// <userdoc>
        /// Which collider groups this component collides with. With nothing selected, it collides with all groups. This can't be changed at runtime.
        /// </userdoc>
        /// /// <remarks>
        /// The collider will still produce events, to allow non trigger rigidbodies or static colliders to act as a trigger if required for certain filtering groups.
        /// </remarks>
        [DataMember(40)]
        [Display("Collides with...")]
        [DefaultValue(CollisionFilterGroupFlags.AllFilter)]
        public CollisionFilterGroupFlags CanCollideWith { get; set; } = CollisionFilterGroupFlags.AllFilter;

        /// <summary>
        /// When updating the associated TransformComponent, should we not set rotation?
        /// </summary>
        [DataMember(69)]
        public bool IgnorePhysicsRotation = false;

        [DataMemberIgnore]
        public Vector3? LocalPhysicsOffset = null;

        /// <summary>
        /// Gets or sets the tag.
        /// </summary>
        /// <value>
        /// The tag.
        /// </value>
        [DataMemberIgnore]
        public string Tag { get; set; }

        [DataMember]
        public float FrictionCoefficient = 0.5f;

        [DataMember]
        public float MaximumRecoveryVelocity = 2f;

        [DataMember]
        public SpringSettings SpringSettings = new SpringSettings(30f, 1f);

        /// <summary>
        /// Computes the physics transformation from the TransformComponent values
        /// </summary>
        /// <returns></returns>
        internal void DerivePhysicsTransform(ref Matrix fromMatrix, out Matrix outMatrix)
        {
            fromMatrix.Decompose(out Vector3 scale, out Matrix rotation, out Vector3 translation);
            DerivePhysicsTransform(translation, rotation, scale, out outMatrix);
        }

        internal void DerivePhysicsTransform(Vector3? worldPosition, Matrix? worldRotation, Vector3? worldScale, out Matrix outMatrix)
        {
            Vector3 translation = worldPosition ?? Entity.Transform.WorldPosition(), scale;
            Matrix rotation;

            if( worldScale.HasValue ) {
                scale = worldScale.Value;
            } else {
                Entity.Transform.WorldMatrix.GetScale(out scale);
            }

            if (worldRotation.HasValue) {
                rotation = worldRotation.Value;
            } else {
                Entity.Transform.WorldMatrix.GetRotationMatrix(out rotation);
            }

            var translationMatrix = Matrix.Translation(translation);
            Matrix.Multiply(ref rotation, ref translationMatrix, out outMatrix);
        }

        internal virtual void UpdateTransformationComponent() { }

        [DataMemberIgnore]
        public virtual Vector3 Position { get; set; }

        [DataMemberIgnore]
        public virtual Quaternion Rotation { get; set; }

        /// <summary>
        /// Is this a "ghost"? Useful for triggers that detect collisions, but don't cause them
        /// </summary>
        [DataMember]
        public bool GhostBody { get; set; }
    }
}
