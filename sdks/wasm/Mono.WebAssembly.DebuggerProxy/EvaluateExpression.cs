﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

using System.Threading;
using System.IO;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WebAssembly.Net.Debugging {

	internal class EvaluateExpression {
		class FindVariableNMethodCall : CSharpSyntaxWalker {
			public List<IdentifierNameSyntax> variables = new List<IdentifierNameSyntax> ();
			public List<InvocationExpressionSyntax> methodCall = new List<InvocationExpressionSyntax> ();
			public List<object> values = new List<Object> ();
			public override void Visit (SyntaxNode node)
			{
				if (node is IdentifierNameSyntax)
					variables.Add (node as IdentifierNameSyntax);
				if (node is InvocationExpressionSyntax) {
					methodCall.Add (node as InvocationExpressionSyntax);
					throw new Exception ("Method Call is not implemented yet");
				}
				if (node is AssignmentExpressionSyntax)
					throw new Exception ("Assignment is not implemented yet");
				base.Visit (node);
			}
			public async Task<SyntaxTree> ReplaceVars (SyntaxTree syntaxTree, MonoProxy proxy, MessageId msg_id, int scope_id, CancellationToken token)
			{
				foreach (var var in variables) {
					CompilationUnitSyntax root = syntaxTree.GetCompilationUnitRoot ();
					ClassDeclarationSyntax classDeclaration = root.Members.ElementAt (0) as ClassDeclarationSyntax;
					MethodDeclarationSyntax method = classDeclaration.Members.ElementAt (0) as MethodDeclarationSyntax;

					JObject value = await proxy.TryGetVariableValue (msg_id, scope_id, var.Identifier.Text, token);

					if (value == null)
						throw new Exception ("The name \"" + var.Identifier.Text + "\" does not exist in the current context");

					values.Add (ConvertJSToCSharpType (value ["value"] ["value"].ToString (), value ["value"] ["type"].ToString ()));

					var updatedMethod = method.AddParameterListParameters (
						SyntaxFactory.Parameter (
							SyntaxFactory.Identifier (var.Identifier.Text))
							.WithType (SyntaxFactory.ParseTypeName (GetTypeFullName(value["value"]["type"].ToString()))));
					var newRoot = root.ReplaceNode (method, updatedMethod);
					syntaxTree = syntaxTree.WithRootAndOptions (newRoot, syntaxTree.Options);
				}
				return syntaxTree;
			}

			private object ConvertJSToCSharpType (string v, string type)
			{
				switch (type) {
				case "number":
					return Convert.ChangeType (v, typeof (int));
				case "string":
					return v;
				}

				throw new Exception ($"Evaluate of this datatype {type} not implemented yet");
			}

			private string GetTypeFullName (string type)
			{
				switch (type) {
					case "number":
						return typeof (int).FullName;
					case "string":
						return typeof (string).FullName;
				}

				throw new Exception ($"Evaluate of this datatype {type} not implemented yet");
			}
		}

		public static async Task<string> CompileAndRunTheExpression (MonoProxy proxy, MessageId msg_id, int scope_id, string expression, CancellationToken token)
		{
			FindVariableNMethodCall findVarNMethodCall = new FindVariableNMethodCall ();
			string retString;
			SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText (@"
				using System;
				public class CompileAndRunTheExpression
				{
					public string Evaluate()
					{
						return (" + expression + @").ToString(); 
					}
				}");

			CompilationUnitSyntax root = syntaxTree.GetCompilationUnitRoot ();
			ClassDeclarationSyntax classDeclaration = root.Members.ElementAt (0) as ClassDeclarationSyntax;
			MethodDeclarationSyntax methodDeclaration = classDeclaration.Members.ElementAt (0) as MethodDeclarationSyntax;
			BlockSyntax blockValue = methodDeclaration.Body;
			ReturnStatementSyntax returnValue = blockValue.Statements.ElementAt (0) as ReturnStatementSyntax;
			InvocationExpressionSyntax expressionInvocation = returnValue.Expression as InvocationExpressionSyntax;
			MemberAccessExpressionSyntax expressionMember = expressionInvocation.Expression as MemberAccessExpressionSyntax;
			ParenthesizedExpressionSyntax expressionParenthesized = expressionMember.Expression as ParenthesizedExpressionSyntax;
			var expressionTree = expressionParenthesized.Expression;

			findVarNMethodCall.Visit (expressionTree);

			syntaxTree = await findVarNMethodCall.ReplaceVars (syntaxTree, proxy, msg_id, scope_id, token);

			MetadataReference [] references = new MetadataReference []
			{
				MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
				MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
			};

			CSharpCompilation compilation = CSharpCompilation.Create (
				"compileAndRunTheExpression",
				syntaxTrees: new [] { syntaxTree },
				references: references,
				options: new CSharpCompilationOptions (OutputKind.DynamicallyLinkedLibrary));
			using (var ms = new MemoryStream ()) {
				EmitResult result = compilation.Emit (ms);
				ms.Seek (0, SeekOrigin.Begin);
				Assembly assembly = Assembly.Load (ms.ToArray ());
				Type type = assembly.GetType ("CompileAndRunTheExpression");
				object obj = Activator.CreateInstance (type);
				var ret = type.InvokeMember ("Evaluate",
					BindingFlags.Default | BindingFlags.InvokeMethod,
					null,
					obj,
					//new object [] { 10 }
					findVarNMethodCall.values.ToArray ());
				retString = ret.ToString ();
			}
			return retString;
		}
	}
}
