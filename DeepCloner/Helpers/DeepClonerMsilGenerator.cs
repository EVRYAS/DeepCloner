﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace Force.DeepCloner.Helpers
{
	internal static class DeepClonerMsilGenerator
	{
		private static int _methodCounter;

		internal static object GenerateClonerInternal(Type realType, bool asObject)
		{
			// there is no performance penalties to cast objects to concrete type, but we can win in removing other conversions
			var methodType = asObject ? typeof(object) : realType;

			if (DeepClonerSafeTypes.IsTypeSafe(realType, null)) return null;

			var mb = TypeCreationHelper.GetModuleBuilder();
			var dt = new DynamicMethod(
				"DeepObjectCloner_" + realType.Name + "_" + Interlocked.Increment(ref _methodCounter), methodType, new[] { methodType, typeof(DeepCloneState) }, mb, true);

			var il = dt.GetILGenerator();

			GenerateProcessMethod(il, realType, asObject && realType.IsValueType);

			var funcType = typeof(Func<,,>).MakeGenericType(methodType, typeof(DeepCloneState), methodType);

			return dt.CreateDelegate(funcType);
		}

		private static void GenerateProcessMethod(ILGenerator il, Type type, bool unboxStruct)
		{
			if (type.IsArray)
			{
				GenerateProcessArrayMethod(il, type);
				return;
			}

			var typeLocal = il.DeclareLocal(type);
			LocalBuilder structLoc = null;

			if (!type.IsValueType)
			{
				// Formatter services is slightly faster variant, but cannot create ContextBoundObject realizations
				if (type.IsContextful || 1.Equals(1))
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(OpCodes.Call, typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));
					il.Emit(OpCodes.Stloc, typeLocal);
				}
				else
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(OpCodes.Call, typeof(object).GetMethod("GetType"));
					il.Emit(OpCodes.Call, typeof(System.Runtime.Serialization.FormatterServices).GetMethod("GetUninitializedObject"));
					il.Emit(OpCodes.Stloc, typeLocal);
				}
			}
			else
			{
				if (unboxStruct)
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(OpCodes.Unbox_Any, type);
					structLoc = il.DeclareLocal(type);
					il.Emit(OpCodes.Dup);
					il.Emit(OpCodes.Stloc, structLoc);
					il.Emit(OpCodes.Stloc, typeLocal);
				}
				else
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(OpCodes.Stloc, typeLocal);
				}

				// il.Emit(OpCodes.Ldloca_S, typeLocal);
				// il.Emit(OpCodes.Initobj, type);
			}

			// added from -> to binding to ensure reference loop handling
			// structs cannot loop here
			if (type.IsClass)
			{
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldloc, typeLocal);
				il.Emit(OpCodes.Call, typeof(DeepCloneState).GetMethod("AddKnownRef"));
			}

			List<FieldInfo> fi = new List<FieldInfo>();
			var tp = type;
			do
			{
				fi.AddRange(tp.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
				tp = tp.BaseType;
				break;
			}
			while (tp != null);
			


			foreach (var fieldInfo in fi)
			{
				Console.WriteLine(type.Name + " " + fieldInfo.Name);
				if (DeepClonerSafeTypes.IsTypeSafe(fieldInfo.FieldType, null))
				{
					il.Emit(type.IsClass ? OpCodes.Ldloc : OpCodes.Ldloca_S, typeLocal);
					if (structLoc == null) il.Emit(OpCodes.Ldarg_0);
					else il.Emit(OpCodes.Ldloc, structLoc);
					il.Emit(OpCodes.Ldfld, fieldInfo);
					il.Emit(OpCodes.Stfld, fieldInfo);

				}
				else
				{
					il.Emit(type.IsClass ? OpCodes.Ldloc : OpCodes.Ldloca_S, typeLocal);
					if (structLoc == null) il.Emit(OpCodes.Ldarg_0);
					else il.Emit(OpCodes.Ldloc, structLoc);
					il.Emit(OpCodes.Ldfld, fieldInfo);
					il.Emit(OpCodes.Ldarg_1);

					var methodInfo = fieldInfo.FieldType.IsValueType 
						? typeof(DeepClonerGenerator).GetMethod("CloneStructInternal", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(fieldInfo.FieldType)
						: typeof(DeepClonerGenerator).GetMethod("CloneClassInternal", BindingFlags.NonPublic | BindingFlags.Static);
					il.Emit(OpCodes.Call, methodInfo);
					il.Emit(OpCodes.Stfld, fieldInfo);
				}
			}

			il.Emit(OpCodes.Ldloc, typeLocal);
			if (unboxStruct)
				il.Emit(OpCodes.Box, type);
			il.Emit(OpCodes.Ret);
		}

		private static void GenerateProcessArrayMethod(ILGenerator il, Type type)
		{
			// TODO: processing array of structs can be simplified
			var typeLocal = il.DeclareLocal(type);
			var lenLocal = il.DeclareLocal(typeof(int));
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Call, type.GetProperty("Length").GetGetMethod());
			il.Emit(OpCodes.Dup);
			il.Emit(OpCodes.Stloc, lenLocal);
			var elementType = type.GetElementType();
			il.Emit(OpCodes.Newarr, elementType);
			il.Emit(OpCodes.Stloc, typeLocal);

			if (DeepClonerSafeTypes.IsTypeSafe(elementType, null))
			{
				// Array.Copy(from, to, from.Length);
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldloc, typeLocal);
				il.Emit(OpCodes.Ldloc, lenLocal);
				il.Emit(
					OpCodes.Call,
					typeof(Array).GetMethod("Copy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Array), typeof(Array), typeof(int) }, null));
			}
			else
			{
				var methodInfo = elementType.IsValueType
						? typeof(DeepClonerGenerator).GetMethod("CloneStructInternal", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(elementType)
						: typeof(DeepClonerGenerator).GetMethod("CloneClassInternal", BindingFlags.NonPublic | BindingFlags.Static);
				LocalBuilder clonerLocal = null;

				if (type.IsValueType)
				{
					// unsafe struct, no inheritance, so, we can use fixed cloner
					var funcType = typeof(Func<,,>).MakeGenericType(elementType, typeof(DeepCloneState), elementType);
					methodInfo = funcType.GetMethod("Invoke");
					clonerLocal = il.DeclareLocal(funcType);
					il.Emit(OpCodes.Call, typeof(DeepClonerGenerator).GetMethod("GetCloner", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(elementType));
					il.Emit(OpCodes.Stloc, clonerLocal);
				}

				var endLoopLabel = il.DefineLabel();
				var startLoopLabel = il.DefineLabel();
				// using for-loop
				var iLocal = il.DeclareLocal(typeof(int));
				il.Emit(OpCodes.Ldc_I4_0);
				il.Emit(OpCodes.Stloc, iLocal);

				il.MarkLabel(startLoopLabel);

				il.Emit(OpCodes.Ldloc, iLocal);
				il.Emit(OpCodes.Ldloc, lenLocal);
				il.Emit(OpCodes.Bge_S, endLoopLabel);

				// to[i] = Clone(from[i])
				il.Emit(OpCodes.Ldloc, typeLocal); // for save
				il.Emit(OpCodes.Ldloc, iLocal);

				if (clonerLocal != null)
					il.Emit(OpCodes.Ldloc, clonerLocal);

				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Ldloc, iLocal);
				il.Emit(OpCodes.Ldelem, elementType); // get elem

				il.Emit(OpCodes.Ldarg_1);

				il.Emit(OpCodes.Call, methodInfo);
				il.Emit(OpCodes.Stelem, elementType);

				il.Emit(OpCodes.Ldloc, iLocal);
				il.Emit(OpCodes.Ldc_I4_1);
				il.Emit(OpCodes.Add);
				il.Emit(OpCodes.Stloc, iLocal);
				il.Emit(OpCodes.Br_S, startLoopLabel);

				il.MarkLabel(endLoopLabel);
			}

			il.Emit(OpCodes.Ldloc, typeLocal);
			il.Emit(OpCodes.Ret);
		}

		internal static object GenerateConvertor(Type from, Type to)
		{
			var mb = TypeCreationHelper.GetModuleBuilder();

			var dt = new DynamicMethod(
				"DeepObjectConvertor_" + from.Name + "_" + to.Name + "_" + Interlocked.Increment(ref _methodCounter), to, new[] { to, typeof(DeepCloneState) }, mb, true);
			var il = dt.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0); // to
			var isStruct = from.IsValueType;
			if (isStruct)
				il.Emit(OpCodes.Unbox_Any, from);
			il.Emit(OpCodes.Ldarg_1); // state
			var realMethod =
				typeof(DeepClonerGenerator).GetMethod(isStruct ? "CloneStructInternal" : "CloneClassInternal", BindingFlags.NonPublic | BindingFlags.Static)
											.MakeGenericMethod(from);

			il.Emit(OpCodes.Call, realMethod);
			if (isStruct)
				il.Emit(OpCodes.Box, from);
			il.Emit(OpCodes.Ret);
			var funcType = typeof(Func<,,>).MakeGenericType(to, typeof(DeepCloneState), to);

			return dt.CreateDelegate(funcType);
		}

		internal static Func<object, object> GenerateMemberwiseCloner()
		{
			// only non-null classes. it is ok such simple implementation
			var dt = new DynamicMethod(
				"ShallowObjectCloner_" + Interlocked.Increment(ref _methodCounter), typeof(object), new[] { typeof(object) }, TypeCreationHelper.GetModuleBuilder(), true);

			var il = dt.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Call, typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));
			il.Emit(OpCodes.Ret);

			return (Func<object, object>)dt.CreateDelegate(typeof(Func<object, object>));
		}
	}
}