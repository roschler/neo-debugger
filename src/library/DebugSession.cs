﻿using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Neo.VM;
using NeoDebug.Models;
using NeoDebug.VariableContainers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NeoDebug
{
    internal class DebugSession : IVariableContainerSession
    {
        private readonly IExecutionEngine engine;
        private readonly Dictionary<int, HashSet<int>> breakPoints = new Dictionary<int, HashSet<int>>();
        private readonly Dictionary<int, IVariableContainer> variableContainers = new Dictionary<int, IVariableContainer>();

        public Contract Contract { get; }

        public VMState EngineState => engine.State;

        public IEnumerable<StackItem> GetResults() => engine.ResultStack;

        public DebugSession(IExecutionEngine engine, Contract contract, ContractArgument[] arguments)
        {
            this.engine = engine;
            Contract = contract;

            using (var builder = contract.BuildInvokeScript(arguments))
            {
                engine.LoadScript(builder.ToArray());
            }
        }

        public IEnumerable<Breakpoint> SetBreakpoints(Source source, IReadOnlyList<SourceBreakpoint> sourceBreakpoints)
        {
            var sourcePath = Path.GetFullPath(source.Path).ToLowerInvariant();
            var sourcePathHash = sourcePath.GetHashCode();

            breakPoints[sourcePathHash] = new HashSet<int>();

            if (sourceBreakpoints.Count == 0)
            {
                yield break;
            }

            var sequencePoints = Contract.DebugInfo.Methods
                .SelectMany(m => m.SequencePoints)
                .Where(sp => sourcePath.Equals(Path.GetFullPath(sp.Document), StringComparison.InvariantCultureIgnoreCase))
                .ToArray();

            foreach (var sourceBreakPoint in sourceBreakpoints)
            {
                var sequencePoint = Array.Find(sequencePoints, sp => sp.StartLine == sourceBreakPoint.Line);

                if (sequencePoint != null)
                {
                    breakPoints[sourcePathHash].Add(sequencePoint.Address);

                    yield return new Breakpoint()
                    {
                        Verified = true,
                        Column = sequencePoint.StartColumn,
                        EndColumn = sequencePoint.EndColumn,
                        Line = sequencePoint.StartLine,
                        EndLine = sequencePoint.EndLine,
                        Source = source
                    };
                }
                else
                {
                    yield return new Breakpoint()
                    {
                        Verified = false,
                        Column = sourceBreakPoint.Column,
                        Line = sourceBreakPoint.Line,
                        Source = source
                    };
                }
            }
        }


        const VMState HALT_OR_FAULT = VMState.HALT | VMState.FAULT;

        bool CheckBreakpoint()
        {
            if ((engine.State & HALT_OR_FAULT) == 0)
            {
                var context = engine.CurrentContext;

                if (Contract.ScriptHash.AsSpan().SequenceEqual(context.ScriptHash))
                {
                    var ip = context.InstructionPointer;
                    foreach (var kvp in breakPoints)
                    {
                        if (kvp.Value.Contains(ip))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public void Continue()
        {
            while ((engine.State & HALT_OR_FAULT) == 0)
            {
                engine.ExecuteNext();

                if (CheckBreakpoint())
                {
                    break;
                }
            }
        }

        void Step(Func<int, int, bool> compare)
        {
            int c = engine.InvocationStack.Count;
            while ((engine.State & HALT_OR_FAULT) == 0)
            {
                engine.ExecuteNext();

                if ((engine.State & HALT_OR_FAULT) != 0)
                {
                    break;
                }

                if (CheckBreakpoint())
                {
                    break;
                }

                if (compare(engine.InvocationStack.Count, c) && Contract.CheckSequencePoint(engine.CurrentContext))
                {
                    break;
                }
            }
        }

        public void StepOver()
        {
            Step((currentStackSize, originalStackSize) => currentStackSize <= originalStackSize);
        }

        public void StepIn()
        {
            Step((_, __) => true);
        }

        public void StepOut()
        {
            Step((currentStackSize, originalStackSize) => currentStackSize < originalStackSize);
        }

        public IEnumerable<Thread> GetThreads()
        {
            yield return new Thread(1, "main thread");
        }

        public IEnumerable<StackFrame> GetStackFrames(StackTraceArguments args)
        {
            System.Diagnostics.Debug.Assert(args.ThreadId == 1);

            if ((engine.State & HALT_OR_FAULT) == 0)
            {
                var start = args.StartFrame ?? 0;
                var count = args.Levels ?? int.MaxValue;
                var end = Math.Min(engine.InvocationStack.Count, start + count);

                for (var i = start; i < end; i++)
                {
                    var context = engine.InvocationStack.Peek(i);
                    var method = Contract.GetMethod(context);

                    var frame = new StackFrame()
                    {
                        Id = i,
                        Name = method?.DisplayName ?? "<unknown>",
                        ModuleId = context.ScriptHash,
                    };

                    var sequencePoint = method?.GetCurrentSequencePoint(context);

                    if (sequencePoint != null)
                    {
                        frame.Source = new Source()
                        {
                            Name = Path.GetFileName(sequencePoint.Document),
                            Path = sequencePoint.Document
                        };
                        frame.Line = sequencePoint.StartLine;
                        frame.Column = sequencePoint.StartColumn;
                        frame.EndLine = sequencePoint.EndLine;
                        frame.EndColumn = sequencePoint.EndColumn;
                    }

                    yield return frame;
                }
            }
        }

        public void ClearVariableContainers()
        {
            variableContainers.Clear();
        }

        public int AddVariableContainer(IVariableContainer container)
        {
            var id = container.GetHashCode();
            variableContainers.Add(id, container);
            return id;
        }

        public IEnumerable<Scope> GetScopes(ScopesArguments args)
        {
            if ((engine.State & HALT_OR_FAULT) == 0)
            {
                var context = engine.InvocationStack.Peek(args.FrameId);
                var contextID = AddVariableContainer(
                    new ExecutionContextContainer(this, context, Contract));
                yield return new Scope("Locals", contextID, false);

                var storageID = AddVariableContainer(engine.GetStorageContainer(this));
                yield return new Scope("Storage", storageID, false);
            }
        }

        public IEnumerable<Variable> GetVariables(VariablesArguments args)
        {
            if ((engine.State & HALT_OR_FAULT) == 0)
            {
                if (variableContainers.TryGetValue(args.VariablesReference, out var container))
                {
                    return container.GetVariables();
                }
            }

            return Enumerable.Empty<Variable>();
        }

        public EvaluateResponse Evaluate(EvaluateArguments args)
        {
            if ((engine.State & HALT_OR_FAULT) == 0)
            {
                for (var stackIndex = 0; stackIndex < engine.InvocationStack.Count; stackIndex++)
                {
                    var context = engine.InvocationStack.Peek(stackIndex);
                    if (context.AltStack.Count > 0)
                    {
                        var method = Contract.GetMethod(context);
                        var variables = (Neo.VM.Types.Array)context.AltStack.Peek(0);

                        for (int variableIndex = 0; variableIndex < variables.Count; variableIndex++)
                        {
                            var local = method?.Locals.ElementAtOrDefault(variableIndex);
                            if (local?.Name == args.Expression)
                            {
                                var variable = variables[variableIndex].GetVariable(this, local);
                                return new EvaluateResponse()
                                {
                                    Result = variable.Value,
                                    VariablesReference = variable.VariablesReference,
                                    Type = variable.Type
                                };
                            }
                        }
                    }
                }
            }

            return new EvaluateResponse()
            {
                PresentationHint = new VariablePresentationHint()
                {
                    Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation
                }
            };
        }
    }
}
