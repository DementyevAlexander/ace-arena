using UnityEngine.UIElements;

public sealed class BrokenBinder
{
    public void Bind(VisualElement root)
    {
        var missingButton = root.Q<Button>("MissingButton");
        var duplicateButton = root.Query<Button>("Duplicate").First();
    }
}
