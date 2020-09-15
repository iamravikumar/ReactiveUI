﻿// Copyright (c) 2020 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ReactiveUI.Fody
{
    /// <summary>
    /// Generates the OAPH property helper.
    /// </summary>
    public partial class ModuleWeaver
    {
        internal void ProcessObservableAsPropertyHelper(TypeNode typeNode)
        {
            if (ObservableAsPropertyHelperValueGetMethod is null)
            {
                throw new InvalidOperationException("ObservableAsPropertyHelper.Value method instance is null.");
            }

            if (ObservableAsPropertyHelperType is null)
            {
                throw new InvalidOperationException("ObservableAsPropertyHelper type is null.");
            }

            if (OAPHCreationHelperMixinToPropertyMethod is null)
            {
                throw new InvalidOperationException("OAPHCreationHelper.ToProperty method instance is null.");
            }

            var typeDefinition = typeNode.TypeDefinition;

            var constructors = typeDefinition.Methods.Where(x => x.IsConstructor).ToList();

            foreach (var method in typeDefinition.Methods.Where(x => x.HasBody && !x.IsStatic))
            {
                ProcessMethod(typeNode, method, constructors);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsToFodyPropertyInstruction(Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Call &&
                instruction.Operand is MethodReference methodReference &&
                methodReference.DeclaringType.FullName == "ReactiveUI.Fody.Helpers.ObservableAsPropertyExtensions" &&
                methodReference.Name == "ToFodyProperty";
        }

        private static IEnumerable<Instruction> FindInitializer(FieldDefinition oldFieldDefinition, List<MethodDefinition> constructors)
        {
            // See if there exists an initializer for the auto-property
            foreach (var constructor in constructors)
            {
                var fieldAssignment = constructor.Body.Instructions.SingleOrDefault(x => Equals(x.Operand, oldFieldDefinition));
                if (fieldAssignment != null)
                {
                    yield return fieldAssignment;
                }
            }
        }

        /// <summary>
        /// Will get all the instructions for the passed in "methodInstruction" to get its required parameters.
        /// It will use the instructions pop/push deltas to determine the instructions.
        /// </summary>
        /// <example>
        /// An example input output would be:
        /// ldc.i4 1
        /// ldc.i4 2
        /// add
        /// 'add' requires two parameters on the evaluation stack. So in this case 'add' would be the methodInstruction
        /// and the two ldc.i4 would be the parameter instructions.
        /// It needs to handle more complex cases like loading field or calling methods to get the instructions.
        /// </example>
        /// <param name="parentMethodDefinition">The parent where the instruction is located.</param>
        /// <param name="methodInstruction">The instruction which we want the parameter instructions for.</param>
        /// <returns>A instruction block which contains the method instruction, and the instructions used for generating its parameters.</returns>
        private static InstructionBlock? GetDependentInstructions(MethodDefinition parentMethodDefinition, Instruction methodInstruction)
        {
            var bodyInstructions = parentMethodDefinition.Body.Instructions;

            if (bodyInstructions == null)
            {
                return null;
            }

            // This method has no parameters from the evaluation stack, return early.
            if (methodInstruction.GetPopDelta() == 0)
            {
                return new InstructionBlock(methodInstruction);
            }

            // Get the first instruction from the parent method that holds the method instruction.
            var iterator = bodyInstructions[0];

            if (iterator == null)
            {
                return null;
            }

            // public void Test() // this is the parent.
            // ldarg.0 // this
            // ldc.i4 1
            // ldc.i4 2
            // callvirt MyClass::Test(int32 a, int32 b)

            // InstructionBlock = instruction = MyClass::Test, parameters = idc.i4, idc.i4, ldarg.0
            var evaluationStack = new Stack<InstructionBlock>();
            InstructionBlock? methodBlock = null;
            while (iterator != null)
            {
                var currentBlock = new InstructionBlock(iterator);
                var iteratorPopDelta = iterator.GetPopDelta();
                var iteratorPushDelta = iterator.GetPushDelta();

                if (iteratorPopDelta != 0)
                {
                    for (int i = 0; i < iteratorPopDelta; ++i)
                    {
                        currentBlock.NeededInstructions.Insert(0, evaluationStack.Pop());
                    }
                }

                if (iteratorPushDelta != 0)
                {
                    evaluationStack.Push(currentBlock);
                }

                if (iterator == methodInstruction)
                {
                    methodBlock = currentBlock;
                    break;
                }

                iterator = iterator.Next;
            }

            return methodBlock;
        }

        private void CreateObservable(TypeDefinition typeDefinition, MethodDefinition method, PropertyData propertyData, InstructionBlock instructionBlock)
        {
            if (propertyData.BackingFieldReference == null)
            {
                return;
            }

            var instructions = method.Body.Instructions;
            foreach (var indexMetadata in indexMetadatas)
            {
                instructions.RemoveAt(indexMetadata.Index);
            }

            var oldBackingField = propertyData.BackingFieldReference.Resolve();

            var oaphType = ObservableAsPropertyHelperType.MakeGenericInstanceType(oldBackingField.FieldType);

            var toPropertyMethodCall = OAPHCreationHelperMixinToPropertyMethod!.MakeGenericInstance(typeDefinition, oldBackingField.FieldType);

            // Declare a field to store the property value
            var field = new FieldDefinition("$" + propertyData.PropertyDefinition.Name, FieldAttributes.Private, oaphType);
            typeDefinition.Fields.Add(field);

            var index = instructions.Insert(
                indexMetadatas.Last().Index,
                Instruction.Create(OpCodes.Ldstr, propertyData.PropertyDefinition.Name), // Property Name
                Instruction.Create(OpCodes.Ldarg_0), // source = this
                Instruction.Create(OpCodes.Ldfld, oldBackingField));

            index = instructions.Insert(index, capturedInstructions);

            instructions.Insert(
                index,
                Instruction.Create(OpCodes.Call, toPropertyMethodCall),  // Invoke our OAPH create method.
                Instruction.Create(OpCodes.Stfld, field));

            MakePropertyObservable(propertyData, field, oldBackingField.FieldType);
        }

        private void MakePropertyObservable(PropertyData propertyData, FieldDefinition field, TypeReference oldFieldType)
        {
            propertyData.PropertyDefinition.SetMethod = null;

            var instructions = propertyData.PropertyDefinition.GetMethod.Body.Instructions;

            instructions.Clear();

            instructions.Add(
                Instruction.Create(OpCodes.Ldarg_0), // this pointer.
                Instruction.Create(OpCodes.Ldfld, field), // Load field
                Instruction.Create(OpCodes.Callvirt, ObservableAsPropertyHelperValueGetMethod!.MakeGeneric(oldFieldType)), // Call the .Value
                Instruction.Create(OpCodes.Ret)); // Return the value.
        }

        private void ProcessMethod(TypeNode typeNode, MethodDefinition method, List<MethodDefinition> constructors)
        {
            method.Body.SimplifyMacros();

            var instructions = method.Body.Instructions;

            var ilProcessor = method.Body.GetILProcessor();

            for (int i = 0; i < instructions.Count; ++i)
            {
                var instruction = instructions[i];
                if (!IsToFodyPropertyInstruction(instruction))
                {
                    continue;
                }

                // Get the instructions for the set property.
                var instructionBlock = GetDependentInstructions(method, instruction.Next);

                if (instructionBlock == null)
                {
                    WriteError($"Method {method.FullName} calls ToFodyProperty() but property couldn't be matched. Make sure that you set ToFodyProperty() to a property. It is ineligible for ToFodyProperty weaving.");
                    return;
                }

                if (!(instructionBlock.Instruction.Operand is MethodDefinition propertyMethod && propertyMethod.IsSetter))
                {
                    WriteError($"Method {method.FullName} calls ToFodyProperty() but property couldn't be matched. Make sure that you set ToFodyProperty() to a property. It is ineligible for ToFodyProperty weaving.");
                    return;
                }

                var name = propertyMethod.Name;

                var propertyData = typeNode.PropertyDatas.Find(x => x.PropertyDefinition.Name == name);

                if (propertyData == null)
                {
                    WriteError($"Method {method.FullName} calls ToFodyProperty() but property couldn't be matched. Make sure that you set ToFodyProperty() to a property. It is ineligible for ToFodyProperty weaving.");
                    return;
                }

                if (propertyData.PropertyDefinition.GetMethod == null)
                {
                    WriteError($"Method {method.FullName} calls ToFodyProperty on property {propertyData.PropertyDefinition.FullName} has no getter and therefore is not suitable for ToFodyProperty weaving.");
                    return;
                }

                if (propertyData.PropertyDefinition.GetMethod.IsStatic)
                {
                    WriteError($"Method {method.FullName} calls ToFodyProperty on property {propertyData.PropertyDefinition.FullName} which getter is static and therefore is not suitable for ToFodyProperty weaving.");
                    return;
                }

                CreateObservable(typeNode.TypeDefinition, method, propertyData, instructionBlock);
            }

            method.Body.OptimizeMacros();
        }

        private class InstructionBlock
        {
            public InstructionBlock(Instruction instruction)
            {
                Instruction = instruction;
                NeededInstructions = new List<InstructionBlock>(instruction.GetPopDelta());
            }

            public List<InstructionBlock> NeededInstructions { get; }

            public Instruction Instruction { get; set; }

            public override string ToString()
            {
                return $"{Instruction} - ({string.Join(", ", NeededInstructions.Select(x => x.ToString()))})";
            }
        }
    }
}
