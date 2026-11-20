using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using static Spv.Specification;

namespace Ryujinx.Graphics.Shader.CodeGen.Spirv
{
    using SpvInstruction = Spv.Generator.Instruction;
    using SpvLiteralInteger = Spv.Generator.LiteralInteger;

    /// <summary>
    /// Switch 2 (NVN) task and mesh programs for VK_EXT_mesh_shader. They talk to the hardware through the
    /// stage buffer (see <see cref="Nx2MeshInfo"/>): here the task's output region is the task payload, and
    /// a mesh program's writes to its output attribute and map regions go straight to the mesh outputs (Metal
    /// keeps mesh outputs in threadgroup memory, 32 KiB on Apple GPUs: staging the regions there too would not
    /// fit Mario Kart World's main mesh program). The epilogue (reached by every invocation, see
    /// EmitterContext.PrepareForReturn) culls the primitives past the count the program wrote. Adapted from
    /// Lichii's emit_spirv_mesh.cpp.
    /// </summary>
    class Nx2MeshCodeGen
    {
        private readonly struct LineTarget
        {
            public readonly SpvInstruction Variable;
            public readonly int Element; // vector element, or -1 for a scalar output
            public readonly bool IsFloat;

            public LineTarget(SpvInstruction variable, int element, bool isFloat)
            {
                Variable = variable;
                Element = element;
                IsFloat = isFloat;
            }
        }

        private const int MaxMeshWorkgroups = 1024;

        private readonly Nx2MeshInfo _info;
        private readonly bool _isTask;

        private SpvInstruction _payload;
        private SpvInstruction _payloadU32;
        private SpvInstruction _primitiveCount;
        private SpvInstruction _primitiveIndices;
        private SpvInstruction _outputPosition;
        private SpvInstruction _workgroupId;
        private SpvInstruction _localInvocationId;

        private readonly List<LineTarget> _vertexTargets = [];
        private readonly List<LineTarget> _primitiveTargets = [];

        // Generic outputs the next stage reads that no line provides: written as zero.
        private readonly List<SpvInstruction> _zeroGenerics = [];

        private Nx2MeshCodeGen(Nx2MeshInfo info, bool isTask)
        {
            _info = info;
            _isTask = isTask;
        }

        /// <summary>
        /// Adds the built-in inputs task and mesh programs need, before the declarations.
        /// </summary>
        public static void AddIoDefinitions(StructuredProgramInfo info)
        {
            info.IoDefinitions.Add(new IoDefinition(StorageKind.Input, IoVariable.CtaId));
            info.IoDefinitions.Add(new IoDefinition(StorageKind.Input, IoVariable.ThreadId));
        }

        public static Nx2MeshCodeGen Declare(CodeGenContext context)
        {
            Nx2MeshCodeGen mesh = new(
                context.Definitions.Nx2Mesh,
                context.Definitions.Nx2Mesh.Stage == ShaderStage.Task);

            mesh.DeclareVariables(context);

            return mesh;
        }

        private SpvInstruction U32(CodeGenContext context, int value) => context.Constant(context.TypeU32(), value);

        private SpvInstruction DeclareVariable(CodeGenContext context, SpvInstruction type, StorageClass storageClass, string name)
        {
            SpvInstruction variable = context.Variable(context.TypePointer(storageClass, type), storageClass);

            context.AddGlobalVariable(variable);
            context.Name(variable, name);

            return variable;
        }

        private SpvInstruction DeclareOutputArray(CodeGenContext context, SpvInstruction elementType, int length, BuiltIn? builtIn, int location, bool perPrimitive, string name)
        {
            SpvInstruction variable = DeclareVariable(context, context.TypeArray(elementType, U32(context, Math.Max(length, 1))), StorageClass.Output, name);

            if (builtIn.HasValue)
            {
                context.Decorate(variable, Decoration.BuiltIn, (SpvLiteralInteger)(int)builtIn.Value);
            }
            else
            {
                context.Decorate(variable, Decoration.Location, (SpvLiteralInteger)location);
            }

            if (perPrimitive)
            {
                context.Decorate(variable, Decoration.PerPrimitiveEXT);
            }

            return variable;
        }

        private void DeclareVariables(CodeGenContext context)
        {
            _workgroupId = context.Inputs[new IoDefinition(StorageKind.Input, IoVariable.CtaId)];
            _localInvocationId = context.Inputs[new IoDefinition(StorageKind.Input, IoVariable.ThreadId)];

            // The task's output region, which its mesh workgroups read as their input region.
            _payload = DeclareVariable(context, context.TypeArray(context.TypeU32(), U32(context, _info.PayloadWords)), StorageClass.TaskPayloadWorkgroupEXT, "task_payload");
            _payloadU32 = context.TypePointer(StorageClass.TaskPayloadWorkgroupEXT, context.TypeU32());

            if (_isTask)
            {
                return;
            }

            _primitiveCount = DeclareVariable(context, context.TypeU32(), StorageClass.Workgroup, "isbe_primitive_count");

            int maxVertices = _info.MaxVertices;
            int maxPrimitives = _info.MaxPrimitives;

            _primitiveIndices = DeclareOutputArray(context, context.TypeVector(context.TypeU32(), 3), maxPrimitives, BuiltIn.PrimitiveTriangleIndicesEXT, 0, false, "primitive_indices");

            SpvInstruction vec4 = context.TypeVector(context.TypeFP32(), 4);
            SpvInstruction[,] generics = new SpvInstruction[2, 32];
            SpvInstruction pointSize = null, primitiveId = null, layer = null, viewport = null;

            LineTarget PerPrimitiveBuiltIn(ref SpvInstruction variable, BuiltIn builtIn, string name)
            {
                variable ??= DeclareOutputArray(context, context.TypeU32(), maxPrimitives, builtIn, 0, true, name);

                return new LineTarget(variable, -1, false);
            }

            LineTarget Target(int line, bool perPrimitive)
            {
                if (line >= Nx2MeshInfo.Generic0X)
                {
                    int index = (line - Nx2MeshInfo.Generic0X) / 4;
                    int p = perPrimitive ? 1 : 0;

                    generics[p, index] ??= DeclareOutputArray(context, vec4, perPrimitive ? maxPrimitives : maxVertices, null, index, perPrimitive, $"{(perPrimitive ? "primitive" : "vertex")}_out{index}");

                    return new LineTarget(generics[p, index], (line - Nx2MeshInfo.Generic0X) % 4, true);
                }

                if (perPrimitive)
                {
                    return line switch
                    {
                        Nx2MeshInfo.PrimitiveId => PerPrimitiveBuiltIn(ref primitiveId, BuiltIn.PrimitiveId, "primitive_id"),
                        Nx2MeshInfo.Layer => PerPrimitiveBuiltIn(ref layer, BuiltIn.Layer, "primitive_layer"),
                        Nx2MeshInfo.ViewportIndex => PerPrimitiveBuiltIn(ref viewport, BuiltIn.ViewportIndex, "primitive_viewport"),
                        _ => default,
                    };
                }

                if (line >= Nx2MeshInfo.PositionX && line <= Nx2MeshInfo.PositionW)
                {
                    _outputPosition ??= DeclareOutputArray(context, vec4, maxVertices, BuiltIn.Position, 0, false, "position");

                    return new LineTarget(_outputPosition, line - Nx2MeshInfo.PositionX, true);
                }

                if (line == Nx2MeshInfo.PointSize)
                {
                    pointSize ??= DeclareOutputArray(context, context.TypeFP32(), maxVertices, BuiltIn.PointSize, 0, false, "point_size");

                    return new LineTarget(pointSize, -1, true);
                }

                return default; // per-primitive values in vertex lines have no vertex output
            }

            foreach (int line in _info.VertexLines)
            {
                _vertexTargets.Add(Target(line, false));
            }

            foreach (int line in _info.PrimitiveLines)
            {
                _primitiveTargets.Add(Target(line, true));
            }

            // The fragment program may read generics no line provides: they must still be outputs here.
            int missing = context.AttributeUsage.NextUsedInputAttributes;

            for (int index = 0; index < 32; index++)
            {
                if ((missing & (1 << index)) != 0 && generics[0, index] == null && generics[1, index] == null)
                {
                    _zeroGenerics.Add(DeclareOutputArray(context, vec4, maxVertices, null, index, false, $"vertex_out{index}_zero"));
                }
            }
        }

        private void Barrier(CodeGenContext context)
        {
            context.ControlBarrier(
                U32(context, (int)Scope.Workgroup),
                U32(context, (int)Scope.Workgroup),
                U32(context, (int)(MemorySemanticsMask.WorkgroupMemory | MemorySemanticsMask.AcquireRelease)));
        }

        private SpvInstruction LoadX(CodeGenContext context, SpvInstruction builtIn)
        {
            SpvInstruction vector = context.Load(context.TypeVector(context.TypeU32(), 3), builtIn);

            return context.CompositeExtract(context.TypeU32(), vector, (SpvLiteralInteger)0);
        }

        private SpvInstruction Clamped(CodeGenContext context, SpvInstruction index, int count)
        {
            return context.GlslUMin(context.TypeU32(), index, U32(context, count - 1));
        }

        private SpvInstruction PayloadWord(CodeGenContext context, SpvInstruction index)
        {
            return context.AccessChain(_payloadU32, _payload, Clamped(context, index, _info.PayloadWords));
        }

        /// <summary>
        /// Runs <paramref name="body"/> under <c>if (condition)</c>.
        /// </summary>
        private static void If(CodeGenContext context, SpvInstruction condition, Action body)
        {
            SpvInstruction thenLabel = context.Label();
            SpvInstruction mergeLabel = context.Label();

            context.SelectionMerge(mergeLabel, SelectionControlMask.MaskNone);
            context.BranchConditional(condition, thenLabel, mergeLabel);
            context.AddLabel(thenLabel);
            body();
            context.Branch(mergeLabel);
            context.AddLabel(mergeLabel);
        }

        public OperationResult GenerateIsbeRead(CodeGenContext context, AstOperation operation)
        {
            SpvInstruction workgroup = LoadX(context, _workgroupId);

            if (_isTask)
            {
                return new OperationResult(AggregateType.U32, workgroup); // the input region holds the workgroup index
            }

            SpvInstruction address = context.GetU32(operation.GetSource(0));
            SpvInstruction index = context.ShiftRightLogical(context.TypeU32(), address, U32(context, 2));
            SpvInstruction word = context.Load(context.TypeU32(), PayloadWord(context, index));
            SpvInstruction isIndex = context.IEqual(context.TypeBool(), address, U32(context, 0));

            return new OperationResult(AggregateType.U32, context.Select(context.TypeU32(), isIndex, workgroup, word));
        }

        public OperationResult GenerateIsbeWrite(CodeGenContext context, AstOperation operation)
        {
            int kind = ((AstOperand)operation.GetSource(0)).Value;
            IAstNode addressNode = operation.GetSource(1);
            SpvInstruction address = context.GetU32(addressNode);
            SpvInstruction value = context.GetU32(operation.GetSource(2));

            switch (kind)
            {
                case Ryujinx.Graphics.Shader.Instructions.InstEmit.Nx2IsbeAttribute:
                    if (_isTask)
                    {
                        SpvInstruction index = context.ShiftRightLogical(context.TypeU32(), address, U32(context, 2));

                        context.Store(PayloadWord(context, index), value);
                    }
                    else
                    {
                        WriteAttribute(context, address, value);
                    }

                    break;
                case Ryujinx.Graphics.Shader.Instructions.InstEmit.Nx2IsbeMapU8:
                    if (!_isTask)
                    {
                        WriteVertexIndex(context, address, value);
                    }

                    break;
                case Ryujinx.Graphics.Shader.Instructions.InstEmit.Nx2IsbeMapU32:
                    // The primitive count: the map is written from its end, so its first word is at byte 3.
                    if (!_isTask)
                    {
                        if (addressNode is AstOperand { Type: OperandType.Constant } constant)
                        {
                            if (constant.Value == 3)
                            {
                                context.Store(_primitiveCount, value);
                            }
                        }
                        else
                        {
                            If(context, context.IEqual(context.TypeBool(), address, U32(context, 3)), () => context.Store(_primitiveCount, value));
                        }
                    }

                    break;
            }

            return OperationResult.Invalid;
        }

        /// <summary>
        /// Subgroup built-ins Metal object and mesh functions lack, computed from the invocation index instead.
        /// </summary>
        public static bool IsEmulatedSubgroupInput(IoVariable ioVariable)
        {
            return ioVariable is IoVariable.SubgroupLaneId or
                IoVariable.SubgroupEqMask or
                IoVariable.SubgroupGeMask or
                IoVariable.SubgroupGtMask or
                IoVariable.SubgroupLeMask or
                IoVariable.SubgroupLtMask;
        }

        // Apple GPUs run 32-wide SIMD groups, filled with consecutive invocations of the (one-dimensional)
        // workgroup: the lane is the invocation index modulo 32.
        private void InitializeSubgroupInputs(CodeGenContext context)
        {
            SpvInstruction u32 = context.TypeU32();
            SpvInstruction lane = context.BitwiseAnd(u32, LoadX(context, _localInvocationId), U32(context, 31));
            SpvInstruction eq = context.ShiftLeftLogical(u32, U32(context, 1), lane);
            SpvInstruction lt = context.ISub(u32, eq, U32(context, 1));
            SpvInstruction le = context.BitwiseOr(u32, lt, eq);

            foreach ((IoDefinition definition, SpvInstruction variable) in context.Inputs)
            {
                if (definition.StorageKind != StorageKind.Input || !IsEmulatedSubgroupInput(definition.IoVariable))
                {
                    continue;
                }

                SpvInstruction value = definition.IoVariable switch
                {
                    IoVariable.SubgroupLaneId => lane,
                    IoVariable.SubgroupEqMask => eq,
                    IoVariable.SubgroupLtMask => lt,
                    IoVariable.SubgroupLeMask => le,
                    IoVariable.SubgroupGtMask => context.Not(u32, le),
                    _ => context.Not(u32, lt), // SubgroupGeMask
                };

                if (definition.IoVariable != IoVariable.SubgroupLaneId)
                {
                    SpvInstruction zero = U32(context, 0);

                    value = context.CompositeConstruct(context.TypeVector(u32, 4), value, zero, zero, zero);
                }

                context.Store(variable, value);
            }
        }

        public OperationResult GeneratePrologue(CodeGenContext context)
        {
            InitializeSubgroupInputs(context);

            // Every invocation stores the zero. For a task: one that culls all its threads never writes the
            // mesh workgroup count, and launching an undefined number of mesh workgroups hangs the GPU.
            if (_isTask)
            {
                context.Store(PayloadWord(context, U32(context, 1)), U32(context, 0));
            }
            else
            {
                context.Store(_primitiveCount, U32(context, 0));

                // Before any output is written: the primitives past the count the program writes are culled
                // in the epilogue.
                context.SetMeshOutputsEXT(U32(context, Math.Max(_info.MaxVertices, 1)), U32(context, Math.Max(_info.MaxPrimitives, 1)));
            }

            Barrier(context);

            return OperationResult.Invalid;
        }

        public OperationResult GenerateEpilogue(CodeGenContext context)
        {
            Barrier(context);

            if (_isTask)
            {
                // Metal bounds a mesh grid (1024 threadgroups on Apple8 GPUs): a larger count faults the GPU.
                SpvInstruction count = context.GlslUMin(context.TypeU32(), context.Load(context.TypeU32(), PayloadWord(context, U32(context, 1))), U32(context, MaxMeshWorkgroups));
                SpvInstruction one = U32(context, 1);

                context.EmitMeshTasksEXT(count, one, one, _payload);
                context.Nx2BlockTerminated = true;

                return OperationResult.Invalid;
            }

            int maxVertices = _info.MaxVertices;
            int maxPrimitives = _info.MaxPrimitives;
            int threads = _info.Threads;

            SpvInstruction primitives = context.GlslUMin(context.TypeU32(), context.Load(context.TypeU32(), _primitiveCount), U32(context, maxPrimitives));
            SpvInstruction invocation = LoadX(context, _localInvocationId);

            if ((_info.ConvertDepth && _outputPosition != null) || _zeroGenerics.Count != 0)
            {
                for (int first = 0; first < maxVertices; first += threads)
                {
                    SpvInstruction vertex = context.IAdd(context.TypeU32(), invocation, U32(context, first));

                    void Finish()
                    {
                        if (_info.ConvertDepth && _outputPosition != null)
                        {
                            ConvertDepth(context, vertex);
                        }

                        SpvInstruction vec4 = context.TypeVector(context.TypeFP32(), 4);

                        foreach (SpvInstruction zero in _zeroGenerics)
                        {
                            context.Store(context.AccessChain(context.TypePointer(StorageClass.Output, vec4), zero, vertex), context.ConstantNull(vec4));
                        }
                    }

                    if (first + threads > maxVertices)
                    {
                        If(context, context.ULessThan(context.TypeBool(), vertex, U32(context, maxVertices)), Finish);
                    }
                    else
                    {
                        Finish();
                    }
                }
            }

            // The primitives past the count: degenerate triangles, which the rasterizer drops.
            for (int first = 0; first < maxPrimitives; first += threads)
            {
                SpvInstruction primitive = context.IAdd(context.TypeU32(), invocation, U32(context, first));
                SpvInstruction outside = context.UGreaterThanEqual(context.TypeBool(), primitive, primitives);

                if (first + threads > maxPrimitives)
                {
                    outside = context.LogicalAnd(context.TypeBool(), outside, context.ULessThan(context.TypeBool(), primitive, U32(context, maxPrimitives)));
                }

                If(context, outside, () =>
                {
                    SpvInstruction uvec3 = context.TypeVector(context.TypeU32(), 3);
                    SpvInstruction pointer = context.AccessChain(context.TypePointer(StorageClass.Output, uvec3), _primitiveIndices, primitive);

                    context.Store(pointer, context.ConstantNull(uvec3));
                });
            }

            return OperationResult.Invalid;
        }

        /// <summary>
        /// A word written to the output attribute region: groups of 32 vertices, then groups of 32 primitives,
        /// one 32-word line per attribute. Stored to the output the line names, for that vertex or primitive.
        /// </summary>
        private void WriteAttribute(CodeGenContext context, SpvInstruction address, SpvInstruction value)
        {
            SpvInstruction u32 = context.TypeU32();
            SpvInstruction word = context.ShiftRightLogical(u32, address, U32(context, 2));
            int primitiveBase = _info.PrimitiveBase;

            void Region(SpvInstruction regionWord, int lineCount, int maxElements, List<LineTarget> targets)
            {
                if (lineCount == 0)
                {
                    return;
                }

                SpvInstruction groupWords = U32(context, lineCount * 32);
                SpvInstruction group = context.UDiv(u32, regionWord, groupWords);
                SpvInstruction within = context.UMod(u32, regionWord, groupWords);
                SpvInstruction line = context.ShiftRightLogical(u32, within, U32(context, 5));
                SpvInstruction element = context.IAdd(u32, context.ShiftLeftLogical(u32, group, U32(context, 5)), context.BitwiseAnd(u32, within, U32(context, 31)));

                If(context, context.ULessThan(context.TypeBool(), element, U32(context, maxElements)), () =>
                {
                    SpvInstruction mergeLabel = context.Label();
                    SpvInstruction[] labels = new SpvInstruction[lineCount];
                    Spv.Generator.IOperand[] cases = new Spv.Generator.IOperand[lineCount * 2];

                    for (int index = 0; index < lineCount; index++)
                    {
                        labels[index] = context.Label();
                        cases[index * 2] = (SpvLiteralInteger)index;
                        cases[index * 2 + 1] = labels[index];
                    }

                    context.SelectionMerge(mergeLabel, SelectionControlMask.MaskNone);
                    context.Switch(line, mergeLabel, cases);

                    for (int index = 0; index < lineCount; index++)
                    {
                        context.AddLabel(labels[index]);

                        LineTarget target = targets[index];

                        if (target.Variable != null)
                        {
                            SpvInstruction stored = target.IsFloat ? context.Bitcast(context.TypeFP32(), value) : value;
                            SpvInstruction pointerType = context.TypePointer(StorageClass.Output, target.IsFloat ? context.TypeFP32() : u32);
                            SpvInstruction pointer = target.Element < 0
                                ? context.AccessChain(pointerType, target.Variable, element)
                                : context.AccessChain(pointerType, target.Variable, element, U32(context, target.Element));

                            context.Store(pointer, stored);
                        }

                        context.Branch(mergeLabel);
                    }

                    context.AddLabel(mergeLabel);
                });
            }

            SpvInstruction isVertex = context.ULessThan(context.TypeBool(), word, U32(context, primitiveBase));

            If(context, isVertex, () => Region(word, _info.VertexLines.Length, _info.MaxVertices, _vertexTargets));
            If(context, context.LogicalNot(context.TypeBool(), isVertex), () =>
                Region(context.ISub(u32, word, U32(context, primitiveBase)), _info.PrimitiveLines.Length, _info.MaxPrimitives, _primitiveTargets));
        }

        /// <summary>
        /// A byte written to the map region past its count word: vertex index (byte - 4) % 3 of primitive (byte - 4) / 3.
        /// </summary>
        private void WriteVertexIndex(CodeGenContext context, SpvInstruction address, SpvInstruction value)
        {
            SpvInstruction u32 = context.TypeU32();
            SpvInstruction inMap = context.UGreaterThanEqual(context.TypeBool(), address, U32(context, 4));

            If(context, inMap, () =>
            {
                SpvInstruction offset = context.ISub(u32, address, U32(context, 4));
                SpvInstruction primitive = context.UDiv(u32, offset, U32(context, 3));
                SpvInstruction element = context.UMod(u32, offset, U32(context, 3));

                If(context, context.ULessThan(context.TypeBool(), primitive, U32(context, _info.MaxPrimitives)), () =>
                {
                    SpvInstruction pointer = context.AccessChain(context.TypePointer(StorageClass.Output, u32), _primitiveIndices, primitive, element);

                    context.Store(pointer, context.BitwiseAnd(u32, value, U32(context, 0xff)));
                });
            });
        }

        // OpenGL-style clip space: z = (z + w) / 2, from the position the program wrote.
        private void ConvertDepth(CodeGenContext context, SpvInstruction vertex)
        {
            SpvInstruction f32 = context.TypeFP32();
            SpvInstruction pointerType = context.TypePointer(StorageClass.Output, f32);
            SpvInstruction zPointer = context.AccessChain(pointerType, _outputPosition, vertex, U32(context, 2));
            SpvInstruction wPointer = context.AccessChain(pointerType, _outputPosition, vertex, U32(context, 3));
            SpvInstruction z = context.FMul(f32, context.FAdd(f32, context.Load(f32, zPointer), context.Load(f32, wPointer)), context.Constant(f32, 0.5f));

            context.Store(zPointer, z);
        }
    }
}
