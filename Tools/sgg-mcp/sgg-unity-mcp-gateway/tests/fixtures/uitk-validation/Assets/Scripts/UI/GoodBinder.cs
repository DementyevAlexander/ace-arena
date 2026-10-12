using UnityEngine.UIElements;

public sealed class GoodBinder
{
    public void Bind(VisualElement root)
    {
        var startButton = root.Q<Button>("StartButton");
        var titleLabel = root.Query<Label>("TitleLabel").First();
    }
}
