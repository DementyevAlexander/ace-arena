# Validation Checklist

Before acting on a Unity project:

```text
[ ] sgg_unity_projects was called, or the user provided a known project id.
[ ] The selected project id is explicit in every Gateway call.
[ ] No active-project assumption is being used.
[ ] The command exists in sgg_unity_describe for this project.
[ ] The command schema was checked for non-trivial args_json.
[ ] Policy allows the command, or confirmation is requested from the user.
[ ] Any paths are Unity-relative Assets/... or Packages/... paths.
[ ] Feature-gated and package-contributed commands are not assumed.
[ ] Write, destructive, network, runtime, and dynamic C# actions have explicit user intent.
[ ] Output, frame count, timeout, and batch scope are bounded.
```

After acting, run the smallest relevant validation and inspect logs if the result is incomplete, stale, or unexpected.
