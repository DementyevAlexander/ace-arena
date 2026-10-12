# Dynamic CSharp

Dynamic Unity Editor C# execution is high risk. Prefer regular catalog commands over `editor.execute_csharp.*`.

Use dynamic C# only when all of these are true:

- The command is discoverable in the selected project's catalog.
- The schema supports the intended operation.
- Policy allows it or the user approves the exact confirmation request.
- A safer purpose-built command is unavailable.

Safety rules:

- Prefer readonly execution for inspection.
- Keep snippets short and output bounded.
- Do not spawn processes.
- Do not access the network.
- Do not touch arbitrary absolute paths.
- Do not modify project files unless the command, policy, and user approval explicitly cover that action.
- Use jobs for long-running execution.

Never use dynamic C# to bypass policy, PathGuard, denied commands, feature flags, or missing command schemas.

Snippet contract: `code` is inserted as the body of `public static object Execute()`. Use object-compatible returns and keep results small.

Output-only example:

```csharp
Mcp.Print(Selection.activeObject?.name);
return null;
```

Value/object examples:

```csharp
return Mcp.Result(42);
```

```csharp
return new Dictionary<string, object> { { "count", Selection.objects.Length } };
```
