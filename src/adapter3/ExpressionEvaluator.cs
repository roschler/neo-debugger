using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Neo;
using Neo.BlockchainToolkit;
using Neo.BlockchainToolkit.Models;
using OneOf;
using StackItem = Neo.VM.Types.StackItem;
using StackItemType = Neo.VM.Types.StackItemType;

namespace NeoDebug.Neo3
{
    class ExpressionEvaluator
    {
        public const string ARG_SLOTS_PREFIX = "#arg";
        public const string EVAL_STACK_PREFIX = "#eval";
        public const string LOCAL_SLOTS_PREFIX = "#local";
        public const string RESULT_STACK_PREFIX = "#result";
        public const string STATIC_SLOTS_PREFIX = "#static";
        public const string STORAGE_PREFIX = "#storage";

        public static readonly IReadOnlyDictionary<string, CastOperation> CastOperations = new Dictionary<string, CastOperation>()
        {
            { "int", CastOperation.Integer },
            { "integer", CastOperation.Integer },
            { "bool", CastOperation.Boolean },
            { "boolean", CastOperation.Boolean },
            { "string", CastOperation.String },
            { "str", CastOperation.String },
            { "hex", CastOperation.HexString },
            { "byte[]", CastOperation.ByteArray },
            { "addr", CastOperation.Address },
        };

        readonly IExecutionContext context;
        readonly IReadOnlyList<StackItem> resultStack;
        readonly StorageContainerBase storageContainer;
        readonly DebugInfo? debugInfo;
        readonly byte addressVersion;

        public ExpressionEvaluator(IApplicationEngine engine, IExecutionContext context, DebugInfo? debugInfo)
        {
            this.context = context;
            this.resultStack = engine.ResultStack;
            this.storageContainer = engine.GetStorageContainer(context.ScriptHash);
            this.debugInfo = debugInfo;
            this.addressVersion = engine.AddressVersion;
        }

        public bool TryEvaluate(IVariableManager manager, EvaluateArguments args, [MaybeNullWhen(false)] out EvaluateResponse response)
        {
            var (castOperation, expression) = ParseCastOperation(args.Expression.AsMemory());
            if (TryEvaluate(expression, out var result, out var resultType, out var remaining))
            {
                if (!remaining.IsEmpty)
                {
                    response = new EvaluateResponse($"\"{new string(remaining.Span)}\" expression not implemented", 0)
                        .AsFailedEval();
                    return true;
                }
                return TryCreateResponse(manager, castOperation, result, resultType, out response);
            }

            response = null;
            return false;
        }

        static bool IsValidRemaining(ReadOnlyMemory<char> expression) => expression.IsEmpty || expression.Span[0] == '.' || expression.Span[0] == '[';

        static bool TryEvaluateIndexedSlot(ReadOnlyMemory<char> expression, string prefix, IReadOnlyList<StackItem> slot, [MaybeNullWhen(false)] out StackItem result, out ReadOnlyMemory<char> remaining)
        {
            if (expression.StartsWith(prefix))
            {
                expression = expression.Slice(prefix.Length);

                var pos = 0;
                while (pos < expression.Length && char.IsDigit(expression.Span[pos]))
                {
                    pos++;
                }

                if (pos > 0)
                {
                    var slotIndexExpr = expression.Slice(0, pos);
                    expression = expression.Slice(pos);
                    if (IsValidRemaining(expression)
                        && int.TryParse(slotIndexExpr.Span, out var slotIndex)
                        && slotIndex < slot.Count)
                    {
                        result = slot[slotIndex];
                        remaining = expression.Slice(slotIndexExpr.Length);
                        return true;
                    }
                }
            }

            remaining = default;
            result = default;
            return false;
        }

        static bool TryEvaluateNamedSlot(ReadOnlyMemory<char> expression, IReadOnlyList<StackItem> slot, IReadOnlyList<DebugInfo.SlotVariable> variables,
            out (StackItem item, string type) result, out ReadOnlyMemory<char> remaining)
        {
            for (int i = 0; i < variables.Count; i++)
            {
                var variable = variables[i];
                if (expression.StartsWith(variable.Name) && variable.Index < slot.Count)
                {
                    remaining = expression.Slice(variable.Name.Length);
                    if (IsValidRemaining(remaining))
                    {
                        result = (slot[variable.Index], variable.Type);
                        return true;
                    }
                }
            }

            result = default;
            remaining = default;
            return false;
        }

        bool TryEvaluate(ReadOnlyMemory<char> expression, [MaybeNullWhen(false)] out StackItem result, out ContractType? resultType, out ReadOnlyMemory<char> remaining)
        {
            if (expression.StartsWith("#"))
            {
                if (storageContainer.TryEvaluate(expression, out result, out resultType, out remaining)) return true;
                resultType = null;
                if (TryEvaluateIndexedSlot(expression, ARG_SLOTS_PREFIX, context.Arguments, out result, out remaining)) return true;
                if (TryEvaluateIndexedSlot(expression, EVAL_STACK_PREFIX, context.EvaluationStack, out result, out remaining)) return true;
                if (TryEvaluateIndexedSlot(expression, LOCAL_SLOTS_PREFIX, context.LocalVariables, out result, out remaining)) return true;
                if (TryEvaluateIndexedSlot(expression, RESULT_STACK_PREFIX, resultStack, out result, out remaining)) return true;
                if (TryEvaluateIndexedSlot(expression, STATIC_SLOTS_PREFIX, context.StaticFields, out result, out remaining)) return true;
            }
            else if (debugInfo is not null)
            {
                // TODO: flow type info out
                resultType = null;
                if (TryEvaluateNamedSlot(expression, context.StaticFields, debugInfo.StaticVariables, out var _result, out remaining))
                {
                    result = _result.item;
                    return true;
                }

                if (debugInfo.TryGetMethod(context.InstructionPointer, out var method))
                {
                    if (TryEvaluateNamedSlot(expression, context.Arguments, method.Parameters, out _result, out remaining))
                    {
                        result = _result.item;
                        return true;

                    }

                    if (TryEvaluateNamedSlot(expression, context.LocalVariables, method.Variables, out _result, out remaining))
                    {
                        result = _result.item;
                        return true;
                    }
                }
            }

            result = default;
            resultType = null;
            remaining = default;
            return false;
        }

        bool TryCreateResponse(IVariableManager manager, CastOperation castOperation, StackItem result, ContractType? resultType, [MaybeNullWhen(false)] out EvaluateResponse response)
        {
            switch (castOperation)
            {
                case CastOperation.None:
                    {
                        var variable = resultType is null
                            ? result.AsVariable(manager, string.Empty)
                            : result.AsVariable(manager, string.Empty, resultType, addressVersion);
                        response = new EvaluateResponse(variable.Value, variable.VariablesReference);
                        return true;
                    }
                case CastOperation.Address:
                    {
                        var hash = new UInt160(result.GetSpan());
                        var address = Neo.Wallets.Helper.ToAddress(hash, addressVersion);
                        response = new EvaluateResponse(address, 0);
                        return true;
                    }
                case CastOperation.HexString:
                    {
                        if (result.IsNull)
                        {
                            response = new EvaluateResponse("<null>", 0);
                            return true;
                        }
                        else
                        {
                            response = new EvaluateResponse(result.AsReadOnlyMemory().Span.ToHexString(), 0);
                            return true;
                        }
                    }
                case CastOperation.Boolean:
                    {
                        response = new EvaluateResponse($"{result.GetBoolean()}", 0);
                        return true;
                    }
                case CastOperation.Integer:
                    {
                        response = new EvaluateResponse($"{result.GetInteger()}", 0);
                        return true;
                    }
                case CastOperation.String:
                    {
                        response = new EvaluateResponse(result.GetString(), 0);
                        return true;
                    }
                case CastOperation.ByteArray:
                    {
                        if (result.IsNull)
                        {
                            response = new EvaluateResponse("<null>", 0);
                            return true;
                        }
                        else
                        {
                            var container = new ByteArrayContainer(result.AsReadOnlyMemory());
                            response = new EvaluateResponse($"{result.Type}", manager.Add(container));
                            return true;
                        }
                    }
                default:
                    {
                        response = new EvaluateResponse($"Unrecognized Cast Operation {castOperation}", 0)
                            .AsFailedEval();
                        return true;
                    }
            }
        }

        static (CastOperation castOperation, ReadOnlyMemory<char> remaining) ParseCastOperation(ReadOnlyMemory<char> expression)
        {

            if (expression.Length >= 1 && expression.Span[0] == '(')
            {
                expression = expression.Slice(1);
                foreach (var (key, operation) in CastOperations)
                {
                    if (expression.StartsWith(key) && expression.Span[key.Length] == ')')
                    {
                        return (operation, expression.Slice(key.Length + 1));
                    }
                }

                throw new Exception("invalid cast operation");
            }

            return (CastOperation.None, expression);
        }
    }
}
