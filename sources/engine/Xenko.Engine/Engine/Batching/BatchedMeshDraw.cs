using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Xenko.Core.Mathematics;
using Xenko.Core.Threading;
using Xenko.Graphics;
using Xenko.Rendering;
using Xenko.Rendering.Rendering;

namespace Xenko.Engine
{
    /// <summary>
    /// Helper class to refer to a BatchedMeshDraw index (can get its matrix, scale etc.)
    /// </summary>
    public class BatchedMeshDrawReference
    {
        public BatchedMeshDraw batchedMeshDraw { get; private set; }
        public int index { get; private set; }

        /// <summary>
        /// Returns true if this points to a correct and valid copy index
        /// </summary>
        public bool IsValidReference => index > -1 && index < batchedMeshDraw.Capacity;

        public Matrix TransformMatrix
        {
            get => batchedMeshDraw.GetTransform(index);
            set => batchedMeshDraw.SetTransform(index, value);
        }
        public Vector2 UVOffset
        {
            get => batchedMeshDraw.GetUVOffset(index);
            set => batchedMeshDraw.SetUVOffset(index, value);
        }
        public Vector2 UVScale
        {
            get => batchedMeshDraw.GetUVScale(index);
            set => batchedMeshDraw.SetUVScale(index, value);
        }
        public Color4 ColorTint
        {
            get => batchedMeshDraw.GetColorTint(index);
            set => batchedMeshDraw.SetColorTint(index, value);
        }

        /// <summary>
        /// This not only hides this index, it invalidates it (since once hidden, it could be reused elsewhere without this reference knowing)
        /// </summary>
        public void Hide()
        {
            if (index != -1)
            {
                batchedMeshDraw.HideIndex(index);
                index = -1;
            }
        }

        /// <summary>
        /// Finds a new index for this index, if possible. This does nothing if there already is a valid index.
        /// </summary>
        /// <returns>true if a new copy index was found (or we already have one). False if no index could be found</returns>
        public bool FindNewIndex()
        {
            if (index == -1)
                index = batchedMeshDraw.GetAvailableIndex();

            return index != -1;
        }

        /// <summary>
        /// Make a reference to a copy in batchedMeshDraw
        /// </summary>
        /// <param name="batchedMeshDraw">Which batch to reference copies of?</param>
        /// <param name="index">Which index to be a reference to? Provide -1 to find one automatically</param>
        public BatchedMeshDrawReference(BatchedMeshDraw batchedMeshDraw, int index = -1)
        {
            this.batchedMeshDraw = batchedMeshDraw;
            this.index = index == -1 ? batchedMeshDraw.GetAvailableIndex() : index;
        }
    }

    /// <summary>
    /// This is like GPU instancing, but handled on the CPU-side to make it easier to implement and use.
    /// It tracks the mesh and moves parts only as needed. Generally thread-safe, except don't make changes while rendering is happening.
    /// Can be linked with other TransformComponents for easy updating "instanced" copies via each TransformComponent's BatchedMeshDrawReference
    /// Needs one Entity with a ModelComponent to do drawing of all "instances", and you can use GenerateDynamicBatchedModel to provide the Model for dynamic batching
    /// </summary>
    public class BatchedMeshDraw : MeshDraw, IDisposable
    {
        /// <summary>
        /// If we set custom colors, are we multiplying or setting directly?
        /// </summary>
        public enum COLOR_MODE
        {
            MULTIPLY = 0, // original color * custom color
            SET = 1 // custom color direct set
        };

        /// <summary>
        /// Defaults to multiplying colors
        /// </summary>
        public COLOR_MODE UseColorTintMode = COLOR_MODE.MULTIPLY;

        /// <summary>
        /// Set an instance at a certain transform matrix
        /// </summary>
        /// <param name="index">Which one to use?</param>
        /// <param name="transform">WorldTransform</param>
        public void SetTransform(int index, Matrix transform)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (internalTransforms[index] != transform)
            {
                internalTransforms[index] = transform;
                MarkUpdateNeeded(index);

                if (index == PossibleFreeIndex)
                    PossibleFreeIndex = (PossibleFreeIndex + 1) % internalTransforms.Length;
            }
        }

        /// <summary>
        /// Gets where this index is, or returns a certain zero-scaled matrix if it is hidden
        /// </summary>
        /// <param name="index">Which one</param>
        /// <returns>Current matrix of index</returns>
        public Matrix GetTransform(int index)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            return internalTransforms[index];
        }

        /// <summary>
        /// Is this index hidden?
        /// </summary>
        /// <param name="index">Which one</param>
        /// <returns>true if hidden</returns>
        public bool IsHidden(int index)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            return internalTransforms[index] == dontdraw;
        }

        /// <summary>
        /// Gets the UV offset of this index, if we are doing that kinda thing
        /// </summary>
        /// <param name="index">which one</param>
        /// <returns>UV offset, if any</returns>
        public Vector2 GetUVOffset(int index)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (uvOffsets == null) return Vector2.Zero;
            return uvOffsets[index];
        }

        /// <summary>
        /// Gets the UV offset of this index, if we are doing that kinda thing
        /// </summary>
        /// <param name="index">which one</param>
        /// <returns>UV offset, if any</returns>
        public Vector2 GetUVScale(int index)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (uvScales == null) return Vector2.One;
            return uvScales[index];
        }

        /// <summary>
        /// Gets the custom color of this index, if any
        /// </summary>
        /// <param name="index">which one</param>
        /// <returns>Color, if any</returns>
        public Color4 GetColorTint(int index)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (colors == null) return Color4.White;
            return colors[index];
        }

        /// <summary>
        /// You can give individual "instances" a UV offset, so copied models use different sections of a texture
        /// </summary>
        /// <param name="index">Which index to offset UVs by?</param>
        /// <param name="offset">How much to offset the UVs?</param>
        public void SetUVOffset(int index, Vector2 offset)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (uvOffsets == null) uvOffsets = new Vector2[internalTransforms.Length];

            if (uvOffsets[index] != offset)
            {
                uvOffsets[index] = offset;
                MarkUpdateNeeded(index);
            }
        }

        /// <summary>
        /// You can give individual "instances" a UV scale, so copied models use different scales of a texture
        /// </summary>
        /// <param name="index">Which index to scale UVs by?</param>
        /// <param name="scale">How much to scale the UVs?</param>
        public void SetUVScale(int index, Vector2 scale)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (uvScales == null)
            {
                // gotta initialize all of these to 1...
                uvScales = new Vector2[internalTransforms.Length];
                for (int i = 0; i < internalTransforms.Length; i++) {
                    uvScales[i].X = 1f;
                    uvScales[i].Y = 1f;
                }
            }

            if (uvScales[index] != scale)
            {
                uvScales[index] = scale;
                MarkUpdateNeeded(index);
            }
        }

        /// <summary>
        /// You can give individual "instances" color/tints if the shader is using vertex colors instead of textures
        /// </summary>
        /// <param name="index">Which index to set a custom color?</param>
        /// <param name="color">What should the custom color tint be?</param>
        public void SetColorTint(int index, Color4 color)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (colors == null)
            {
                // initialize everything to white, so it doesn't black everything else out
                colors = new Color4[internalTransforms.Length];
                for (int i = 0; i < colors.Length; i++) colors[i] = Color4.White;
            }

            if (colors[index] != color)
            {
                colors[index] = color;
                MarkUpdateNeeded(index);
            }
        }

        /// <summary>
        /// Uses a built-in "zero" matrix to hide this index instance
        /// </summary>
        /// <param name="index">Which one to hide</param>
        public void HideIndex(int index)
        {
            if (index < 0 || index >= internalTransforms.Length)
                throw new IndexOutOfRangeException("BatchedMeshDraw out of bounds: index " + index + " is invalid (length is " + internalTransforms.Length + ")");

            if (internalTransforms[index] != dontdraw)
            {
                internalTransforms[index] = dontdraw;
                MarkUpdateNeeded(index);
            }

            PossibleFreeIndex = index;
        }

        /// <summary>
        /// If true, only copies within the camera's frustum will be updated.
        /// May cause issues with shadows, but improves performance. Make sure you set "AssociatedTransform" if this is not a root entity, so this
        /// can be calculated correctly.
        /// </summary>
        public bool OptimizeForFrustum = true;

        /// <summary>
        /// If we are opimizing for frustum, and we are NOT a root entity, we need to know how these matricies are transformed to know if they are
        /// within camera boundaries.
        /// </summary>
        public TransformComponent AssociatedTransform;

        /// <summary>
        /// Returns how many objects this BatchedMeshDraw can maintain. If you go over this number, you'll get out-of-bound exceptions.
        /// </summary>
        public int Capacity => internalTransforms.Length;

        /// <summary>
        /// Get a copy index that is currently hidden and is ready to be used
        /// </summary>
        /// <returns>index of free one. -1 if none could be found</returns>
        public int GetAvailableIndex()
        {
            for (int i=0; i<internalTransforms.Length; i++)
            {
                int checkindex = (i + PossibleFreeIndex) % internalTransforms.Length;
                if (internalTransforms[checkindex] == dontdraw)
                    return checkindex;
            }

            return -1;
        }

        /// <summary>
        /// Easy function for making dynamic models. Make sure to do this during initialization and not runtime, as it is a bit slow.
        /// </summary>
        /// <param name="baseModel">What is the model we will be copying?</param>
        /// <param name="capacity">How many do we want to be able to draw at once?</param>
        /// <param name="bounds">What are the maximum rendering bounds for this?</param>
        /// <param name="batchManager">Output used for updating transforms of each copy</param>
        /// <returns></returns>
        public static Model GenerateDynamicBatchedModel(Model baseModel, int capacity, BoundingBox bounds, out BatchedMeshDraw batchManager)
        {
            batchManager = new BatchedMeshDraw(baseModel, capacity);
            Model mod = new Xenko.Rendering.Model();
            Xenko.Rendering.Mesh m = new Xenko.Rendering.Mesh(batchManager, new ParameterCollection());
            m.BoundingBox = mod.BoundingBox = bounds;
            mod.Add(m);
            mod.Add(baseModel.Materials[0]);
            return mod;
        }

        /// <summary>
        /// Hides all of the copies, resetting this BatchedMeshDraw to an empty state
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < internalTransforms.Length; i++)
                HideIndex(i);

            PossibleFreeIndex = 0;
        }

        /// <summary>
        /// Raw creation of a BatchedMeshDraw object. I recommend using GenerateDynamicBatchedModel instead for ease.
        /// Make sure to do this during initialization and not runtime, as it is a bit slow.
        /// </summary>
        /// <param name="m">Base model that will be copied</param>
        /// <param name="capacity">How mnay to prepare and be able to draw at once?</param>
        public BatchedMeshDraw(Model m, int capacity)
        {
            if (m.Materials.Count != 1)
                throw new ArgumentException("Model needs exactly 1 material for proper batching.");

            // calculate what a single mesh looks like and store its original verts
            Model singleMesh = ModelBatcher.BatchModel(m);

            if (singleMesh.Meshes.Count != 1)
                throw new ArgumentException("Model couldn't be flattened into 1 mesh. Are you sure the vertex & index buffers have been captured?");

            StagedMeshDraw singleDraw = singleMesh.Meshes[0].Draw as StagedMeshDraw;
            if (singleDraw.Verticies is VertexPositionNormalTextureTangent[] vppntt)
                origVerts0 = vppntt;
            else if (singleDraw.Verticies is VertexPositionNormalColor[] vpnc)
                origVerts1 = vpnc;

            if (m.BoundingBox == BoundingBox.Empty) m.UpdateBoundingBox();
            originalBoundingBox = m.BoundingBox;

            internalTransforms = new Matrix[capacity];
            for (int i = 0; i < capacity; i++)
                internalTransforms[i] = dontdraw;

            tempHiding = new bool[capacity];
            avoidDuplicates = new bool[capacity];
            indexUpdatesRequired = new int[capacity];
            proccessingUpdates = new int[capacity];
            actuallyUpdated = new int[capacity];
            Model batched = ModelBatcher.GenerateBatch(singleMesh, new List<Matrix>(internalTransforms));
            smd = (StagedMeshDraw)batched.Meshes[0].Draw;
            updateVerts = UpdateVertexBuffer;
            uploadVerts = UploadNewVertexBuffers;

            singleDraw.Dispose();
        }

        private SpinLock duplicateChecker = new SpinLock();

        private void MarkUpdateNeeded(int index)
        {
            bool lockTaken = false;
            try
            {
                duplicateChecker.Enter(ref lockTaken);

                if (!avoidDuplicates[index])
                {
                    avoidDuplicates[index] = true;
                    indexUpdatesRequired[tCount++] = index;
                }
            }
            finally
            {
                if (lockTaken) duplicateChecker.Exit();
            }
        }

        internal void UploadNewVertexBuffers(CommandList commandList)
        {
            // only update buffers if something changed
            int uploadCount = Interlocked.Exchange(ref auCount, 0);

            if (uploadCount > 0)
            {
                object origVerts = origVerts0 == null ? origVerts1 : origVerts0;
                int len = origVerts0 == null ? origVerts1.Length : origVerts0.Length;

                // find the actual range of the buffer we need to upload
                Array.Sort(actuallyUpdated, 0, uploadCount);

                int startIndex = actuallyUpdated[0] * len;
                int endIndex = (1 + actuallyUpdated[uploadCount - 1]) * len;

                if (origVerts is VertexPositionNormalTextureTangent[])
                    smd.VertexBuffers[0].Buffer.FastRawSetData<VertexPositionNormalTextureTangent>(commandList, (VertexPositionNormalTextureTangent[])smd.Verticies, startIndex, endIndex);
                else
                    smd.VertexBuffers[0].Buffer.FastRawSetData<VertexPositionNormalColor>(commandList, (VertexPositionNormalColor[])smd.Verticies, startIndex, endIndex);
            }
        }

        internal void UpdateVertexBuffer(BoundingFrustum frustum)
        {
            if (smd == null || (smd.VertexBuffers?[0].Buffer?.Ready ?? false) == false) return; // not ready yet
            int tUpdateCount = Interlocked.Exchange(ref tCount, 0);
            if (tUpdateCount == 0) return;
            if (tUpdateCount > indexUpdatesRequired.Length) tUpdateCount = indexUpdatesRequired.Length;

            // use a copy of this array, so we don't step over it during multithreaded events
            Array.Copy(indexUpdatesRequired, proccessingUpdates, tUpdateCount);

            object origVerts = origVerts0 == null ? origVerts1 : origVerts0;
            int len = origVerts0 == null ? origVerts1.Length : origVerts0.Length;
            int auRunningcount = 0;

            Xenko.Core.Threading.Dispatcher.For(0, tUpdateCount, (i) =>
            {
                int index = proccessingUpdates[i];
                avoidDuplicates[index] = false;
                Matrix tMatrix = internalTransforms[index];
                if (OptimizeForFrustum)
                {
                    BoundingBox newbb;
                    if (AssociatedTransform == null)
                        BoundingBox.Transform(ref originalBoundingBox, ref tMatrix, out newbb);
                    else
                    {
                        Matrix adjusted = tMatrix * AssociatedTransform.WorldMatrix;
                        BoundingBox.Transform(ref originalBoundingBox, ref adjusted, out newbb);
                    }
                    if (frustum.Contains(ref newbb) == false)
                    {
                        // this isn't in the camera's frustum, so check next frame if it is
                        MarkUpdateNeeded(index);
                        if (tempHiding[index]) return;
                        tempHiding[index] = true;
                        tMatrix = tempskipdraw; // update this to hide it
                    }
                    else tempHiding[index] = false;
                }
                actuallyUpdated[Interlocked.Increment(ref auRunningcount) - 1] = index;
                Vector2 uvOffset = uvOffsets == null ? Vector2.Zero : uvOffsets[index];
                Vector2 uvScale = uvScales == null ? Vector2.One : uvScales[index];
                Color4? specialColor = colors == null ? null : colors[index];
                tMatrix.GetScale(out var tMatrixScale);
                int vPos = index * len;
                int vertsPerThread = (int)Math.Ceiling((float)len / (float)Xenko.Core.Threading.Dispatcher.MaxDegreeOfParallelism);
                if (origVerts is VertexPositionNormalTextureTangent[] ov)
                {
                    Xenko.Core.Threading.Dispatcher.For(0, Xenko.Core.Threading.Dispatcher.MaxDegreeOfParallelism, (t) =>
                    {
                        int start = vPos + t * vertsPerThread;
                        for (int j = start; j < start + vertsPerThread && j < vPos + len; j++)
                        {
                            ref VertexPositionNormalTextureTangent ovr = ref ov[j - vPos];
                            ref VertexPositionNormalTextureTangent endvert = ref ((VertexPositionNormalTextureTangent[])smd.Verticies)[j];
                            Vector3 origPos = ovr.Position;
                            Vector3 origNom = ovr.Normal;
                            endvert.Position.X = (origPos.X * tMatrix.M11) + (origPos.Y * tMatrix.M21) + (origPos.Z * tMatrix.M31) + tMatrix.M41;
                            endvert.Position.Y = (origPos.X * tMatrix.M12) + (origPos.Y * tMatrix.M22) + (origPos.Z * tMatrix.M32) + tMatrix.M42;
                            endvert.Position.Z = (origPos.X * tMatrix.M13) + (origPos.Y * tMatrix.M23) + (origPos.Z * tMatrix.M33) + tMatrix.M43;
                            endvert.Normal.X = ((origNom.X * tMatrix.M11) + (origNom.Y * tMatrix.M21) + (origNom.Z * tMatrix.M31)) / tMatrixScale.X;
                            endvert.Normal.Y = ((origNom.X * tMatrix.M12) + (origNom.Y * tMatrix.M22) + (origNom.Z * tMatrix.M32)) / tMatrixScale.Y;
                            endvert.Normal.Z = ((origNom.X * tMatrix.M13) + (origNom.Y * tMatrix.M23) + (origNom.Z * tMatrix.M33)) / tMatrixScale.Z;
                            endvert.Tangent = ovr.Tangent;
                            endvert.TextureCoordinate.X = ovr.TextureCoordinate.X * uvScale.X + uvOffset.X;
                            endvert.TextureCoordinate.Y = ovr.TextureCoordinate.Y * uvScale.Y + uvOffset.Y;
                        }
                    });
                }
                else
                {
                    var opnc = origVerts as VertexPositionNormalColor[];
                    Xenko.Core.Threading.Dispatcher.For(0, Xenko.Core.Threading.Dispatcher.MaxDegreeOfParallelism, (t) => {
                        int start = vPos + t * vertsPerThread;
                        for (int j = start; j < start + vertsPerThread && j < vPos + len; j++) {
                            ref VertexPositionNormalColor ovr = ref opnc[j - vPos];
                            ref VertexPositionNormalColor endvert = ref ((VertexPositionNormalColor[])smd.Verticies)[j];
                            Vector3 origPos = ovr.Position;
                            Vector3 origNom = ovr.Normal;
                            endvert.Position.X = (origPos.X * tMatrix.M11) + (origPos.Y * tMatrix.M21) + (origPos.Z * tMatrix.M31) + tMatrix.M41;
                            endvert.Position.Y = (origPos.X * tMatrix.M12) + (origPos.Y * tMatrix.M22) + (origPos.Z * tMatrix.M32) + tMatrix.M42;
                            endvert.Position.Z = (origPos.X * tMatrix.M13) + (origPos.Y * tMatrix.M23) + (origPos.Z * tMatrix.M33) + tMatrix.M43;
                            endvert.Normal.X = ((origNom.X * tMatrix.M11) + (origNom.Y * tMatrix.M21) + (origNom.Z * tMatrix.M31)) / tMatrixScale.X;
                            endvert.Normal.Y = ((origNom.X * tMatrix.M12) + (origNom.Y * tMatrix.M22) + (origNom.Z * tMatrix.M32)) / tMatrixScale.Y;
                            endvert.Normal.Z = ((origNom.X * tMatrix.M13) + (origNom.Y * tMatrix.M23) + (origNom.Z * tMatrix.M33)) / tMatrixScale.Z;
                            endvert.Color = specialColor.HasValue ? (UseColorTintMode == COLOR_MODE.MULTIPLY ? ovr.Color * specialColor.Value : specialColor.Value) : ovr.Color;
                        }
                    });
                }
            });
            auCount = auRunningcount;
        }

        public void Dispose()
        {
            if (smd != null)
                smd.Dispose();

            tempHiding = null;
            internalTransforms = null;
            indexUpdatesRequired = null;
            proccessingUpdates = null;
            avoidDuplicates = null;
            uvOffsets = null;
            colors = null;
        }

        ~BatchedMeshDraw()
        {
            Dispose();
        }

        private static Matrix dontdraw = Matrix.Transformation(Vector3.Zero, Quaternion.Identity, Vector3.Zero);
        private static Matrix tempskipdraw = Matrix.Transformation(Vector3.Zero, Quaternion.Identity, Vector3.One);

        public override PrimitiveType PrimitiveType => smd.PrimitiveType;
        public override int DrawCount => smd.DrawCount;
        public override int StartLocation => smd.StartLocation;
        public override VertexBufferBinding[] VertexBuffers => smd.VertexBuffers;
        public override IndexBufferBinding IndexBuffer => smd.IndexBuffer;
        internal override Action<GraphicsDevice, StagedMeshDraw> performStage => smd.performStage;
        internal override StagedMeshDraw getStagedMeshDraw => smd;
        private BoundingBox originalBoundingBox;
        private int PossibleFreeIndex;
        private bool[] tempHiding;

        private Matrix[] internalTransforms;
        private StagedMeshDraw smd;
        private int tCount, auCount;
        private int[] indexUpdatesRequired, proccessingUpdates, actuallyUpdated;
        private bool[] avoidDuplicates;
        private Vector2[] uvOffsets, uvScales;
        private Color4[] colors;

        VertexPositionNormalTextureTangent[] origVerts0;
        VertexPositionNormalColor[] origVerts1;
    }
}
