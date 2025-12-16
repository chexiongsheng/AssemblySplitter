using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace AssemblySplitter
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: AssemblySplitter <assembly_path> <depth> [search_directories]");
                Console.WriteLine("  assembly_path: Path to the assembly to split (e.g., Scripts.GameCore.dll)");
                Console.WriteLine("  depth: Depth parameter (must be >= 1)");
                Console.WriteLine("  search_directories: Optional, semicolon-separated list of directories to search for dependencies");
                return;
            }

            string assemblyPath = args[0];
            if (!int.TryParse(args[1], out int depth) || depth < 1)
            {
                Console.WriteLine("Error: Depth must be an integer >= 1");
                return;
            }

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Error: Assembly not found: {assemblyPath}");
                return;
            }

            // Parse optional search directories
            var searchDirectories = new List<string>();
            if (args.Length >= 3)
            {
                searchDirectories.AddRange(args[2].Split(';', StringSplitOptions.RemoveEmptyEntries));
            }

            var splitter = new AssemblySplitter(assemblyPath, depth, searchDirectories);
            try
            {
                splitter.Split();
                Console.WriteLine("Assembly split completed successfully.");
                Console.WriteLine($"Backup preserved at: {assemblyPath}.backup");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    Console.WriteLine($"Inner stack trace:\n{ex.InnerException.StackTrace}");
                }
                // Rollback on failure
                splitter.Rollback();
            }
        }
    }

    internal class AssemblySplitter
    {
        private readonly string _assemblyPath;
        private readonly int _depth;
        private readonly string _aotAssemblyPath;
        private readonly string _assemblyName;
        private readonly string _backupPath;
        private readonly List<string> _searchDirectories;

        public AssemblySplitter(string assemblyPath, int depth, List<string> searchDirectories = null)
        {
            _assemblyPath = Path.GetFullPath(assemblyPath);
            _depth = depth;
            _assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            _searchDirectories = searchDirectories ?? new List<string>();
            
            string directory = Path.GetDirectoryName(_assemblyPath) ?? ".";
            _aotAssemblyPath = Path.Combine(directory, $"{_assemblyName}.AOT.dll");
            _backupPath = _assemblyPath + ".backup";
        }

        /// <summary>
        /// Rollback changes: restore from backup and delete AOT assembly
        /// </summary>
        public void Rollback()
        {
            Console.WriteLine("\nRolling back changes...");
            try
            {
                // Delete AOT assembly if it exists
                if (File.Exists(_aotAssemblyPath))
                {
                    File.Delete(_aotAssemblyPath);
                    Console.WriteLine($"  Deleted AOT assembly: {_aotAssemblyPath}");
                }

                // Restore from backup if it exists
                if (File.Exists(_backupPath))
                {
                    File.Copy(_backupPath, _assemblyPath, true);
                    Console.WriteLine($"  Restored from backup: {_assemblyPath}");
                    File.Delete(_backupPath);
                    Console.WriteLine($"  Deleted backup: {_backupPath}");
                }

                Console.WriteLine("Rollback completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Rollback failed: {ex.Message}");
            }
        }

        private void AddSearchDirectorys(BaseAssemblyResolver resolver, string baseDirectory)
        {
            if (resolver == null) return;
            resolver.AddSearchDirectory(baseDirectory);
            Console.WriteLine($"  Added search directory: {baseDirectory}");
            
            // Add user-specified search directories
            foreach (var dir in _searchDirectories)
            {
                if (Directory.Exists(dir))
                {
                    resolver.AddSearchDirectory(dir);
                    Console.WriteLine($"  Added search directory: {dir}");
                }
            }
        }

        public void Split()
        {
            // Check if AOT assembly already exists
            if (File.Exists(_aotAssemblyPath))
            {
                throw new InvalidOperationException($"AOT assembly already exists: {_aotAssemblyPath}. Please remove it before splitting.");
            }

            // Create backup
            File.Copy(_assemblyPath, _backupPath, true);
            Console.WriteLine($"Backup created: {_backupPath}");

            // Step 1: Analyze the assembly to determine which types to move
            HashSet<string> typesToMoveToAot;
            var analysisReaderParams = new ReaderParameters
            {
                ReadingMode = ReadingMode.Deferred  // Deferred mode to avoid resolving all dependencies upfront
            };
            using (var assembly = AssemblyDefinition.ReadAssembly(_assemblyPath, analysisReaderParams))
            {
                AddSearchDirectorys(assembly.MainModule.AssemblyResolver as BaseAssemblyResolver, Path.GetDirectoryName(_aotAssemblyPath) ?? ".");
                var dependencyGraph = BuildDependencyGraph(assembly);
                var typeDepths = CalculateTypeDepths(dependencyGraph);
                
                typesToMoveToAot = typeDepths
                    .Where(kvp => kvp.Value <= _depth)
                    .Select(kvp => kvp.Key)
                    .ToHashSet();

                if (typesToMoveToAot.Count == 0)
                {
                    Console.WriteLine("No types found to move at the specified depth.");
                    return;
                }

                Console.WriteLine($"\nTypes to move to AOT assembly ({typesToMoveToAot.Count} types):");
                foreach (var type in typesToMoveToAot.OrderBy(t => typeDepths[t]).ThenBy(t => t))
                {
                    Console.WriteLine($"  [Depth {typeDepths[type]}] {type}");
                }
            }

            // Step 2: Copy assembly to AOT path
            File.Copy(_assemblyPath, _aotAssemblyPath, true);
            Console.WriteLine($"\nCopied assembly to: {_aotAssemblyPath}");

            // Step 3: Modify the AOT assembly - remove types that should NOT be moved (keep only typesToMoveToAot)
            ModifyAotAssembly(typesToMoveToAot);

            // Step 4: Modify the source assembly - remove types that ARE moved, and update references
            ModifySourceAssembly(typesToMoveToAot);

            Console.WriteLine($"\nUpdated source assembly: {_assemblyPath}");
            Console.WriteLine($"Created AOT assembly: {_aotAssemblyPath}");
        }

        /// <summary>
        /// Modify AOT assembly: keep only types that should be moved, remove everything else
        /// </summary>
        private void ModifyAotAssembly(HashSet<string> typesToKeep)
        {
            // Read assembly into memory to avoid file locking issues
            // Use Deferred reading mode to avoid resolving dependencies eagerly
            var readerParams = new ReaderParameters
            {
                ReadingMode = ReadingMode.Deferred,
                InMemory = true
            };

            using var assembly = AssemblyDefinition.ReadAssembly(_aotAssemblyPath, readerParams);

            AddSearchDirectorys(assembly.MainModule.AssemblyResolver as BaseAssemblyResolver, Path.GetDirectoryName(_aotAssemblyPath) ?? ".");

            // Change assembly name
            assembly.Name.Name = $"{_assemblyName}.AOT";
            assembly.MainModule.Name = $"{_assemblyName}.AOT.dll";

            // Collect types to remove (those NOT in typesToKeep)
            var typesToRemove = new List<TypeDefinition>();
            foreach (var type in assembly.MainModule.Types.ToList())
            {
                if (type.Name == "<Module>") continue;
                CollectTypesToRemove(type, typesToKeep, typesToRemove, keepMatching: false);
            }

            // Remove types
            foreach (var type in typesToRemove.Where(t => t.DeclaringType == null))
            {
                //Console.WriteLine($"Removed {type} from AOT assembly");
                assembly.MainModule.Types.Remove(type);
            }

            Console.WriteLine($"Removed {typesToRemove.Count} types from AOT assembly");

            assembly.Write(_aotAssemblyPath);
        }

        /// <summary>
        /// Modify source assembly: remove types that were moved, update references to point to AOT assembly
        /// </summary>
        private void ModifySourceAssembly(HashSet<string> typesToRemove)
        {
            // Read assembly into memory to avoid file locking issues
            // Use Deferred reading mode to avoid resolving dependencies eagerly
            var readerParams = new ReaderParameters
            {
                ReadingMode = ReadingMode.Deferred,
                InMemory = true
            };

            using var assembly = AssemblyDefinition.ReadAssembly(_assemblyPath, readerParams);

            AddSearchDirectorys(assembly.MainModule.AssemblyResolver as BaseAssemblyResolver, Path.GetDirectoryName(_aotAssemblyPath) ?? ".");

            // Add reference to AOT assembly
            var aotRef = new AssemblyNameReference($"{_assemblyName}.AOT", assembly.Name.Version);
            assembly.MainModule.AssemblyReferences.Add(aotRef);

            // Update all type references that point to types now in AOT assembly
            UpdateTypeReferences(assembly, typesToRemove, aotRef);

            // Import types from AOT assembly that are still referenced
            ImportReferencedTypes(assembly, typesToRemove, aotRef);

            // Collect types to remove
            var typesToDelete = new List<TypeDefinition>();
            foreach (var type in assembly.MainModule.Types.ToList())
            {
                if (type.Name == "<Module>") continue;
                CollectTypesToRemove(type, typesToRemove, typesToDelete, keepMatching: true);
            }

            // Remove types
            foreach (var type in typesToDelete.Where(t => t.DeclaringType == null))
            {
                assembly.MainModule.Types.Remove(type);
            }

            Console.WriteLine($"Removed {typesToDelete.Count} types from source assembly");

            assembly.Write(_assemblyPath);
        }

        /// <summary>
        /// Import types that have been moved to the AOT assembly but are still referenced
        /// </summary>
        private void ImportReferencedTypes(AssemblyDefinition assembly, HashSet<string> movedTypes, AssemblyNameReference aotRef)
        {
            var module = assembly.MainModule;
            
            // Collect all type references that need to be imported
            foreach (var type in module.Types.ToList())
            {
                if (type.Name == "<Module>") continue;
                ImportReferencedTypesInType(type, movedTypes, aotRef, module);
            }
        }

        private void ImportReferencedTypesInType(TypeDefinition type, HashSet<string> movedTypes,
            AssemblyNameReference aotRef, ModuleDefinition module)
        {
            // Import generic parameter constraints
            foreach (var genericParam in type.GenericParameters)
            {
                for (int i = 0; i < genericParam.Constraints.Count; i++)
                {
                    var constraint = genericParam.Constraints[i];
                    if (ContainsMovedType(constraint.ConstraintType, movedTypes))
                    {
                        genericParam.Constraints[i] = new GenericParameterConstraint(
                            ImportTypeReference(constraint.ConstraintType, movedTypes, aotRef, module));
                    }
                }
            }

            // Import base type if needed - use ContainsMovedType to catch nested generic types
            if (type.BaseType != null && ContainsMovedType(type.BaseType, movedTypes))
            {
                type.BaseType = ImportTypeReference(type.BaseType, movedTypes, aotRef, module);
            }

            // Import interfaces - use ContainsMovedType to catch nested generic types
            for (int i = 0; i < type.Interfaces.Count; i++)
            {
                var iface = type.Interfaces[i];
                if (ContainsMovedType(iface.InterfaceType, movedTypes))
                {
                    type.Interfaces[i] = new InterfaceImplementation(
                        ImportTypeReference(iface.InterfaceType, movedTypes, aotRef, module));
                }
            }

            // Import field types - check for any type that contains moved types (including nested in generics)
            foreach (var field in type.Fields)
            {
                if (ContainsMovedType(field.FieldType, movedTypes))
                {
                    field.FieldType = ImportTypeReference(field.FieldType, movedTypes, aotRef, module);
                }
            }

            // Import method signatures - check for any type that contains moved types (including nested in generics)
            foreach (var method in type.Methods)
            {
                // Import method generic parameter constraints
                foreach (var genericParam in method.GenericParameters)
                {
                    for (int i = 0; i < genericParam.Constraints.Count; i++)
                    {
                        var constraint = genericParam.Constraints[i];
                        if (ContainsMovedType(constraint.ConstraintType, movedTypes))
                        {
                            genericParam.Constraints[i] = new GenericParameterConstraint(
                                ImportTypeReference(constraint.ConstraintType, movedTypes, aotRef, module));
                        }
                    }
                }

                if (ContainsMovedType(method.ReturnType, movedTypes))
                {
                    method.ReturnType = ImportTypeReference(method.ReturnType, movedTypes, aotRef, module);
                }

                foreach (var param in method.Parameters)
                {
                    if (ContainsMovedType(param.ParameterType, movedTypes))
                    {
                        param.ParameterType = ImportTypeReference(param.ParameterType, movedTypes, aotRef, module);
                    }
                }

                if (method.HasBody)
                {
                    foreach (var variable in method.Body.Variables)
                    {
                        if (ContainsMovedType(variable.VariableType, movedTypes))
                        {
                            variable.VariableType = ImportTypeReference(variable.VariableType, movedTypes, aotRef, module);
                        }
                    }

                    // Process instructions in method body
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is TypeReference typeRef && ContainsMovedType(typeRef, movedTypes))
                        {
                            instruction.Operand = ImportTypeReference(typeRef, movedTypes, aotRef, module);
                        }
                        else if (instruction.Operand is MethodReference methodRef)
                        {
                            ImportMethodReference(methodRef, movedTypes, aotRef, module);
                        }
                        else if (instruction.Operand is FieldReference fieldRef)
                        {
                            ImportFieldReference(fieldRef, movedTypes, aotRef, module);
                        }
                    }
                }
            }

            // Import property types - check for any type that contains moved types (including nested in generics)
            foreach (var property in type.Properties)
            {
                if (ContainsMovedType(property.PropertyType, movedTypes))
                {
                    property.PropertyType = ImportTypeReference(property.PropertyType, movedTypes, aotRef, module);
                }
            }

            // Import event types - check for any type that contains moved types (including nested in generics)
            foreach (var evt in type.Events)
            {
                if (ContainsMovedType(evt.EventType, movedTypes))
                {
                    evt.EventType = ImportTypeReference(evt.EventType, movedTypes, aotRef, module);
                }
            }

            // Import custom attributes on type
            ImportCustomAttributes(type.CustomAttributes, movedTypes, aotRef, module);

            // Import custom attributes on methods, parameters, fields, properties, events
            foreach (var method in type.Methods)
            {
                ImportCustomAttributes(method.CustomAttributes, movedTypes, aotRef, module);
                ImportCustomAttributes(method.MethodReturnType.CustomAttributes, movedTypes, aotRef, module);
                foreach (var param in method.Parameters)
                {
                    ImportCustomAttributes(param.CustomAttributes, movedTypes, aotRef, module);
                }
            }
            foreach (var field in type.Fields)
            {
                ImportCustomAttributes(field.CustomAttributes, movedTypes, aotRef, module);
            }
            foreach (var property in type.Properties)
            {
                ImportCustomAttributes(property.CustomAttributes, movedTypes, aotRef, module);
            }
            foreach (var evt in type.Events)
            {
                ImportCustomAttributes(evt.CustomAttributes, movedTypes, aotRef, module);
            }

            // Process nested types
            foreach (var nestedType in type.NestedTypes)
            {
                ImportReferencedTypesInType(nestedType, movedTypes, aotRef, module);
            }
        }

        private TypeReference ImportTypeReference(TypeReference typeRef, HashSet<string> movedTypes,
            AssemblyNameReference aotRef, ModuleDefinition module)
        {
            if (typeRef == null) return null;

            // Handle generic instance types - ALWAYS recreate to ensure all nested types are imported
            if (typeRef is GenericInstanceType git)
            {
                var elementType = ImportTypeReference(git.ElementType, movedTypes, aotRef, module);
                var newGit = new GenericInstanceType(elementType);
                foreach (var arg in git.GenericArguments)
                {
                    // Always import each generic argument, even if not in movedTypes,
                    // to ensure nested generic types are handled correctly
                    newGit.GenericArguments.Add(ImportTypeReference(arg, movedTypes, aotRef, module));
                }
                return newGit;
            }

            // Handle arrays
            if (typeRef is ArrayType arrayType)
            {
                var elementType = ImportTypeReference(arrayType.ElementType, movedTypes, aotRef, module);
                return new ArrayType(elementType, arrayType.Rank);
            }

            // Handle by-ref types
            if (typeRef is ByReferenceType byRefType)
            {
                var elementType = ImportTypeReference(byRefType.ElementType, movedTypes, aotRef, module);
                return new ByReferenceType(elementType);
            }

            // Handle pointer types
            if (typeRef is PointerType ptrType)
            {
                var elementType = ImportTypeReference(ptrType.ElementType, movedTypes, aotRef, module);
                return new PointerType(elementType);
            }

            // Handle generic parameters - return as is
            if (typeRef is GenericParameter)
            {
                return typeRef;
            }

            // Check if this type needs to be imported from AOT
            if (movedTypes.Contains(typeRef.FullName))
            {
                // Create a new type reference pointing to the AOT assembly
                var newTypeRef = new TypeReference(typeRef.Namespace, typeRef.Name, module, aotRef);
                // Copy generic parameters if any
                if (typeRef.HasGenericParameters)
                {
                    foreach (var gp in typeRef.GenericParameters)
                    {
                        newTypeRef.GenericParameters.Add(new GenericParameter(gp.Name, newTypeRef));
                    }
                }
                return newTypeRef;
            }

            return typeRef;
        }

        private void ImportMethodReference(MethodReference methodRef, HashSet<string> movedTypes,
            AssemblyNameReference aotRef, ModuleDefinition module)
        {
            if (methodRef == null) return;

            // Handle generic instance methods specially - they wrap another method reference
            if (methodRef is GenericInstanceMethod gim)
            {
                // Update the element method's declaring type (not the GenericInstanceMethod directly)
                if (gim.ElementMethod != null && ShouldUpdateReference(gim.ElementMethod.DeclaringType, movedTypes))
                {
                    // For MethodSpecification, we need to update through ElementMethod
                    // but ElementMethod.DeclaringType might also be read-only for some cases
                    // So we update the generic arguments instead
                }

                // Update generic arguments
                for (int i = 0; i < gim.GenericArguments.Count; i++)
                {
                    if (ShouldUpdateReference(gim.GenericArguments[i], movedTypes))
                    {
                        gim.GenericArguments[i] = ImportTypeReference(gim.GenericArguments[i], movedTypes, aotRef, module);
                    }
                }
                return;
            }

            // For other MethodSpecification types, skip DeclaringType modification
            if (methodRef is MethodSpecification)
            {
                return;
            }

            // Update declaring type only for regular method references
            if (ShouldUpdateReference(methodRef.DeclaringType, movedTypes))
            {
                methodRef.DeclaringType = ImportTypeReference(methodRef.DeclaringType, movedTypes, aotRef, module);
            }
        }

        private void ImportFieldReference(FieldReference fieldRef, HashSet<string> movedTypes,
            AssemblyNameReference aotRef, ModuleDefinition module)
        {
            if (fieldRef == null) return;

            // Update declaring type
            if (ShouldUpdateReference(fieldRef.DeclaringType, movedTypes))
            {
                fieldRef.DeclaringType = ImportTypeReference(fieldRef.DeclaringType, movedTypes, aotRef, module);
            }

            // Update field type
            if (ShouldUpdateReference(fieldRef.FieldType, movedTypes))
            {
                fieldRef.FieldType = ImportTypeReference(fieldRef.FieldType, movedTypes, aotRef, module);
            }
        }

        /// <summary>
        /// Import custom attributes that reference moved types
        /// </summary>
        private void ImportCustomAttributes(Mono.Collections.Generic.Collection<CustomAttribute> attributes,
            HashSet<string> movedTypes, AssemblyNameReference aotRef, ModuleDefinition module)
        {
            foreach (var attr in attributes)
            {
                // Import attribute type if needed
                if (ContainsMovedType(attr.AttributeType, movedTypes))
                {
                    // We need to update the constructor reference
                    if (attr.Constructor != null && ShouldUpdateReference(attr.Constructor.DeclaringType, movedTypes))
                    {
                        attr.Constructor.DeclaringType = ImportTypeReference(attr.Constructor.DeclaringType, movedTypes, aotRef, module);
                    }
                }

                // Import constructor arguments that reference moved types
                if (attr.HasConstructorArguments)
                {
                    for (int i = 0; i < attr.ConstructorArguments.Count; i++)
                    {
                        var arg = attr.ConstructorArguments[i];
                        if (ContainsMovedType(arg.Type, movedTypes))
                        {
                            var newType = ImportTypeReference(arg.Type, movedTypes, aotRef, module);
                            attr.ConstructorArguments[i] = new CustomAttributeArgument(newType, arg.Value);
                        }
                        // Also handle TypeReference values
                        if (arg.Value is TypeReference typeRefValue && ContainsMovedType(typeRefValue, movedTypes))
                        {
                            var newTypeValue = ImportTypeReference(typeRefValue, movedTypes, aotRef, module);
                            attr.ConstructorArguments[i] = new CustomAttributeArgument(arg.Type, newTypeValue);
                        }
                    }
                }

                // Import property arguments that reference moved types
                if (attr.HasProperties)
                {
                    for (int i = 0; i < attr.Properties.Count; i++)
                    {
                        var prop = attr.Properties[i];
                        if (ContainsMovedType(prop.Argument.Type, movedTypes))
                        {
                            var newType = ImportTypeReference(prop.Argument.Type, movedTypes, aotRef, module);
                            attr.Properties[i] = new CustomAttributeNamedArgument(
                                prop.Name, new CustomAttributeArgument(newType, prop.Argument.Value));
                        }
                    }
                }

                // Import field arguments that reference moved types
                if (attr.HasFields)
                {
                    for (int i = 0; i < attr.Fields.Count; i++)
                    {
                        var field = attr.Fields[i];
                        if (ContainsMovedType(field.Argument.Type, movedTypes))
                        {
                            var newType = ImportTypeReference(field.Argument.Type, movedTypes, aotRef, module);
                            attr.Fields[i] = new CustomAttributeNamedArgument(
                                field.Name, new CustomAttributeArgument(newType, field.Argument.Value));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Update type references to point to the AOT assembly for types that were moved
        /// </summary>
        private void UpdateTypeReferences(AssemblyDefinition assembly, HashSet<string> movedTypes, AssemblyNameReference aotRef)
        {
            var module = assembly.MainModule;

            // Update type references in all remaining types
            foreach (var type in module.Types)
            {
                if (type.Name == "<Module>") continue;
                UpdateTypeReferencesInType(type, movedTypes, aotRef, module);
            }

            // Update member references
            foreach (var memberRef in module.GetMemberReferences().ToList())
            {
                if (memberRef.DeclaringType != null && ShouldUpdateReference(memberRef.DeclaringType, movedTypes))
                {
                    UpdateTypeReferenceScope(memberRef.DeclaringType, aotRef);
                }
            }

            // Update type references
            foreach (var typeRef in module.GetTypeReferences().ToList())
            {
                if (ShouldUpdateReference(typeRef, movedTypes))
                {
                    UpdateTypeReferenceScope(typeRef, aotRef);
                }
            }
        }

        private void UpdateTypeReferencesInType(TypeDefinition type, HashSet<string> movedTypes, 
            AssemblyNameReference aotRef, ModuleDefinition module)
        {
            // Update base type
            if (type.BaseType != null && ShouldUpdateReference(type.BaseType, movedTypes))
            {
                UpdateTypeReferenceScope(type.BaseType, aotRef);
            }

            // Update interfaces
            foreach (var iface in type.Interfaces)
            {
                if (ShouldUpdateReference(iface.InterfaceType, movedTypes))
                {
                    UpdateTypeReferenceScope(iface.InterfaceType, aotRef);
                }
            }

            // Update fields
            foreach (var field in type.Fields)
            {
                if (ShouldUpdateReference(field.FieldType, movedTypes))
                {
                    UpdateTypeReferenceScope(field.FieldType, aotRef);
                }
            }

            // Update methods
            foreach (var method in type.Methods)
            {
                if (ShouldUpdateReference(method.ReturnType, movedTypes))
                {
                    UpdateTypeReferenceScope(method.ReturnType, aotRef);
                }

                foreach (var param in method.Parameters)
                {
                    if (ShouldUpdateReference(param.ParameterType, movedTypes))
                    {
                        UpdateTypeReferenceScope(param.ParameterType, aotRef);
                    }
                }

                if (method.HasBody)
                {
                    foreach (var variable in method.Body.Variables)
                    {
                        if (ShouldUpdateReference(variable.VariableType, movedTypes))
                        {
                            UpdateTypeReferenceScope(variable.VariableType, aotRef);
                        }
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is TypeReference typeRef && ShouldUpdateReference(typeRef, movedTypes))
                        {
                            UpdateTypeReferenceScope(typeRef, aotRef);
                        }
                        else if (instruction.Operand is MethodReference methodRef)
                        {
                            if (ShouldUpdateReference(methodRef.DeclaringType, movedTypes))
                            {
                                UpdateTypeReferenceScope(methodRef.DeclaringType, aotRef);
                            }
                        }
                        else if (instruction.Operand is FieldReference fieldRef)
                        {
                            if (ShouldUpdateReference(fieldRef.DeclaringType, movedTypes))
                            {
                                UpdateTypeReferenceScope(fieldRef.DeclaringType, aotRef);
                            }
                        }
                    }
                }
            }

            // Update properties
            foreach (var property in type.Properties)
            {
                if (ShouldUpdateReference(property.PropertyType, movedTypes))
                {
                    UpdateTypeReferenceScope(property.PropertyType, aotRef);
                }
            }

            // Update events
            foreach (var evt in type.Events)
            {
                if (ShouldUpdateReference(evt.EventType, movedTypes))
                {
                    UpdateTypeReferenceScope(evt.EventType, aotRef);
                }
            }

            // Process nested types
            foreach (var nestedType in type.NestedTypes)
            {
                UpdateTypeReferencesInType(nestedType, movedTypes, aotRef, module);
            }
        }

        private bool ShouldUpdateReference(TypeReference typeRef, HashSet<string> movedTypes)
        {
            if (typeRef == null) return false;

            // Handle generic instance types
            if (typeRef is GenericInstanceType git)
            {
                return ShouldUpdateReference(git.ElementType, movedTypes);
            }

            // Handle arrays
            if (typeRef is ArrayType arrayType)
            {
                return ShouldUpdateReference(arrayType.ElementType, movedTypes);
            }

            // Handle by-ref and pointer types
            if (typeRef is ByReferenceType byRefType)
            {
                return ShouldUpdateReference(byRefType.ElementType, movedTypes);
            }

            if (typeRef is PointerType ptrType)
            {
                return ShouldUpdateReference(ptrType.ElementType, movedTypes);
            }

            // Check if the type is in our moved types set
            return movedTypes.Contains(typeRef.FullName);
        }

        /// <summary>
        /// Check if a type reference contains any moved types (including in generic arguments)
        /// </summary>
        private bool ContainsMovedType(TypeReference typeRef, HashSet<string> movedTypes)
        {
            if (typeRef == null) return false;

            // Handle generic instance types - check element type AND all generic arguments
            if (typeRef is GenericInstanceType git)
            {
                if (ContainsMovedType(git.ElementType, movedTypes))
                    return true;
                foreach (var arg in git.GenericArguments)
                {
                    if (ContainsMovedType(arg, movedTypes))
                        return true;
                }
                return false;
            }

            // Handle arrays
            if (typeRef is ArrayType arrayType)
            {
                return ContainsMovedType(arrayType.ElementType, movedTypes);
            }

            // Handle by-ref and pointer types
            if (typeRef is ByReferenceType byRefType)
            {
                return ContainsMovedType(byRefType.ElementType, movedTypes);
            }

            if (typeRef is PointerType ptrType)
            {
                return ContainsMovedType(ptrType.ElementType, movedTypes);
            }

            // Skip generic parameters
            if (typeRef is GenericParameter) return false;

            // Check if the type is in our moved types set
            return movedTypes.Contains(typeRef.FullName);
        }

        private void UpdateTypeReferenceScope(TypeReference typeRef, AssemblyNameReference aotRef)
        {
            if (typeRef == null) return;

            // Skip generic parameters - they don't have a settable Scope
            if (typeRef is GenericParameter) return;

            // Handle generic instance types
            if (typeRef is GenericInstanceType git)
            {
                UpdateTypeReferenceScope(git.ElementType, aotRef);
                foreach (var arg in git.GenericArguments)
                {
                    UpdateTypeReferenceScope(arg, aotRef);
                }
                return;
            }

            // Handle arrays
            if (typeRef is ArrayType arrayType)
            {
                UpdateTypeReferenceScope(arrayType.ElementType, aotRef);
                return;
            }

            // Handle by-ref types
            if (typeRef is ByReferenceType byRefType)
            {
                UpdateTypeReferenceScope(byRefType.ElementType, aotRef);
                return;
            }

            // Handle pointer types
            if (typeRef is PointerType ptrType)
            {
                UpdateTypeReferenceScope(ptrType.ElementType, aotRef);
                return;
            }

            // Update the scope to point to AOT assembly
            typeRef.Scope = aotRef;
        }

        /// <summary>
        /// Collect the full names of all types remaining in the module
        /// </summary>
        private void CollectRemainingTypeNames(TypeDefinition type, HashSet<string> typeNames)
        {
            typeNames.Add(type.FullName);
            foreach (var nested in type.NestedTypes)
            {
                CollectRemainingTypeNames(nested, typeNames);
            }
        }

        private void CollectTypesToRemove(TypeDefinition type, HashSet<string> typeSet, 
            List<TypeDefinition> result, bool keepMatching)
        {
            bool inSet = typeSet.Contains(type.FullName);
            bool shouldRemove = keepMatching ? inSet : !inSet;

            if (shouldRemove)
            {
                result.Add(type);
            }
        }

        /// <summary>
        /// Build a dependency graph: for each type, find which other types in the same assembly it references
        /// </summary>
        private Dictionary<string, HashSet<string>> BuildDependencyGraph(AssemblyDefinition assembly)
        {
            var graph = new Dictionary<string, HashSet<string>>();
            var allTypeNames = new HashSet<string>();

            // First, collect all type names in this assembly
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    CollectTypeNames(type, allTypeNames);
                }
            }

            // Build the dependency graph
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    BuildTypeDependencies(type, allTypeNames, graph);
                }
            }

            return graph;
        }

        private void CollectTypeNames(TypeDefinition type, HashSet<string> typeNames)
        {
            if (type.Name == "<Module>") return;
            
            typeNames.Add(type.FullName);
            
            foreach (var nestedType in type.NestedTypes)
            {
                CollectTypeNames(nestedType, typeNames);
            }
        }

        private void BuildTypeDependencies(TypeDefinition type, HashSet<string> allTypeNames, 
            Dictionary<string, HashSet<string>> graph)
        {
            if (type.Name == "<Module>") return;

            var dependencies = new HashSet<string>();
            
            // Check base type
            if (type.BaseType != null)
            {
                AddDependencyIfInAssembly(type.BaseType, allTypeNames, dependencies);
            }

            // Check interfaces
            foreach (var iface in type.Interfaces)
            {
                AddDependencyIfInAssembly(iface.InterfaceType, allTypeNames, dependencies);
            }

            // Check fields
            foreach (var field in type.Fields)
            {
                AddDependencyIfInAssembly(field.FieldType, allTypeNames, dependencies);
                foreach (var attr in field.CustomAttributes)
                {
                    AddDependencyIfInAssembly(attr.AttributeType, allTypeNames, dependencies);
                }
            }

            // Check properties
            foreach (var property in type.Properties)
            {
                AddDependencyIfInAssembly(property.PropertyType, allTypeNames, dependencies);
                foreach (var attr in property.CustomAttributes)
                {
                    AddDependencyIfInAssembly(attr.AttributeType, allTypeNames, dependencies);
                }
            }

            // Check methods
            foreach (var method in type.Methods)
            {
                AddDependencyIfInAssembly(method.ReturnType, allTypeNames, dependencies);
                
                foreach (var param in method.Parameters)
                {
                    AddDependencyIfInAssembly(param.ParameterType, allTypeNames, dependencies);
                }
                foreach (var attr in method.CustomAttributes)
                {
                    AddDependencyIfInAssembly(attr.AttributeType, allTypeNames, dependencies);
                }

                if (method.HasBody)
                {
                    foreach (var variable in method.Body.Variables)
                    {
                        AddDependencyIfInAssembly(variable.VariableType, allTypeNames, dependencies);
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is TypeReference typeRef)
                        {
                            AddDependencyIfInAssembly(typeRef, allTypeNames, dependencies);
                        }
                        else if (instruction.Operand is MethodReference methodRef)
                        {
                            AddDependencyIfInAssembly(methodRef.DeclaringType, allTypeNames, dependencies);
                            AddDependencyIfInAssembly(methodRef.ReturnType, allTypeNames, dependencies);
                            foreach (var param in methodRef.Parameters)
                            {
                                AddDependencyIfInAssembly(param.ParameterType, allTypeNames, dependencies);
                            }
                            var genericMethod = methodRef as GenericInstanceMethod;
                            if (genericMethod != null)
                            {
                                foreach (var arg in genericMethod.GenericArguments)
                                {
                                    AddDependencyIfInAssembly(arg, allTypeNames, dependencies);
                                }
                            }
                        }
                        else if (instruction.Operand is FieldReference fieldRef)
                        {
                            AddDependencyIfInAssembly(fieldRef.DeclaringType, allTypeNames, dependencies);
                            AddDependencyIfInAssembly(fieldRef.FieldType, allTypeNames, dependencies);
                        }
                    }
                }
            }

            // Check custom attributes
            foreach (var attr in type.CustomAttributes)
            {
                AddDependencyIfInAssembly(attr.AttributeType, allTypeNames, dependencies);
            }

            // Check generic parameters
            foreach (var genericParam in type.GenericParameters)
            {
                foreach (var constraint in genericParam.Constraints)
                {
                    AddDependencyIfInAssembly(constraint.ConstraintType, allTypeNames, dependencies);
                }
            }

            foreach (var nestedType in type.NestedTypes)
            {
                AddDependencyIfInAssembly(nestedType, allTypeNames, dependencies);
            }

            // Remove self-reference
            dependencies.Remove(type.FullName);
            
            graph[type.FullName] = dependencies;

            // Process nested types
            foreach (var nestedType in type.NestedTypes)
            {
                BuildTypeDependencies(nestedType, allTypeNames, graph);
            }
        }

        private void AddDependencyIfInAssembly(TypeReference typeRef, HashSet<string> allTypeNames, 
            HashSet<string> dependencies)
        {
            if (typeRef == null) return;

            // Handle generic instances
            if (typeRef is GenericInstanceType genericType)
            {
                AddDependencyIfInAssembly(genericType.ElementType, allTypeNames, dependencies);
                foreach (var arg in genericType.GenericArguments)
                {
                    AddDependencyIfInAssembly(arg, allTypeNames, dependencies);
                }
                return;
            }

            // Handle arrays
            if (typeRef is ArrayType arrayType)
            {
                AddDependencyIfInAssembly(arrayType.ElementType, allTypeNames, dependencies);
                return;
            }

            // Handle by-ref and pointer types
            if (typeRef is ByReferenceType byRefType)
            {
                AddDependencyIfInAssembly(byRefType.ElementType, allTypeNames, dependencies);
                return;
            }

            if (typeRef is PointerType ptrType)
            {
                AddDependencyIfInAssembly(ptrType.ElementType, allTypeNames, dependencies);
                return;
            }

            // Check if the type is in our assembly
            string fullName = typeRef.FullName;
            if (allTypeNames.Contains(fullName))
            {
                dependencies.Add(fullName);
            }
        }

        /// <summary>
        /// Calculate depth for each type. Depth 1 = leaf nodes (no dependencies on other types in assembly)
        /// </summary>
        private Dictionary<string, int> CalculateTypeDepths(Dictionary<string, HashSet<string>> dependencyGraph)
        {
            var depths = new Dictionary<string, int>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();

            foreach (var typeName in dependencyGraph.Keys)
            {
                if (!depths.ContainsKey(typeName))
                {
                    CalculateDepthDFS(typeName, dependencyGraph, depths, visited, visiting);
                }
            }

            return depths;
        }

        private int CalculateDepthDFS(string typeName, Dictionary<string, HashSet<string>> graph,
            Dictionary<string, int> depths, HashSet<string> visited, HashSet<string> visiting)
        {
            if (depths.TryGetValue(typeName, out int cachedDepth))
            {
                return cachedDepth;
            }

            // Detect circular dependency
            if (visiting.Contains(typeName))
            {
                // For circular dependencies, we treat them as depth 1 (they form a cycle)
                return 1;
            }

            visiting.Add(typeName);

            if (!graph.TryGetValue(typeName, out var dependencies) || dependencies.Count == 0)
            {
                // Leaf node - no dependencies
                depths[typeName] = 1;
            }
            else
            {
                int maxDependencyDepth = 0;
                foreach (var dep in dependencies)
                {
                    if (graph.ContainsKey(dep))
                    {
                        int depDepth = CalculateDepthDFS(dep, graph, depths, visited, visiting);
                        maxDependencyDepth = Math.Max(maxDependencyDepth, depDepth);
                    }
                }
                depths[typeName] = maxDependencyDepth + 1;
            }

            visiting.Remove(typeName);
            visited.Add(typeName);

            return depths[typeName];
        }
    }
}
