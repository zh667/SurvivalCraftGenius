using Engine;
using Game;
using SurvivalcraftGenius.Agent;
using SurvivalcraftGenius.Mod;

namespace SurvivalcraftGenius.UI;

/// <summary>
/// LLM backend settings: base URL, model, API key. Values live only on this
/// device (data:/SurvivalcraftGenius/settings.json — editable by hand too).
/// </summary>
public sealed class GeniusSettingsDialog : Dialog
{
    private static readonly Color PanelColor = new(20, 26, 30, 242);
    private static readonly Color OutlineColor = new(96, 120, 128);
    private static readonly Color HintColor = new(150, 160, 170);

    private readonly GeniusPlayerComponent _component;
    private readonly TextBoxWidget _urlBox;
    private readonly TextBoxWidget _modelBox;
    private readonly TextBoxWidget _keyBox;
    private readonly BevelledButtonWidget _saveButton;
    private readonly BevelledButtonWidget _cancelButton;
    private bool _closed;

    public GeniusSettingsDialog(GeniusPlayerComponent component)
    {
        _component = component ?? throw new ArgumentNullException(nameof(component));
        var settings = component.Settings;

        var root = new CanvasWidget
        {
            Size = new Vector2(720f, 470f),
            HorizontalAlignment = WidgetAlignment.Center,
            VerticalAlignment = WidgetAlignment.Center,
        };
        Children.Add(root);
        root.Children.Add(new RectangleWidget
        {
            FillColor = PanelColor,
            OutlineColor = OutlineColor,
        });

        var stack = new StackPanelWidget
        {
            Direction = LayoutDirection.Vertical,
            Margin = new Vector2(16f, 10f),
            HorizontalAlignment = WidgetAlignment.Center,
        };
        root.Children.Add(stack);

        stack.Children.Add(new LabelWidget
        {
            Text = "Genius 设置(OpenAI 兼容后端)",
            FontScale = 0.85f,
            Color = new Color(220, 235, 220),
            Size = new Vector2(680f, 36f),
        });

        _urlBox = AddField(stack, "API 地址(例:https://api.deepseek.com/v1)", settings.ApiBaseUrl);
        _modelBox = AddField(stack, "模型名(例:deepseek-chat)", settings.Model);
        _keyBox = AddField(stack, "API Key", settings.ApiKey);

        stack.Children.Add(new LabelWidget
        {
            Text = "密钥仅保存在本机 data:/SurvivalcraftGenius/settings.json",
            FontScale = 0.55f,
            Color = HintColor,
            Size = new Vector2(680f, 24f),
        });

        var buttonRow = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(0f, 10f),
        };
        stack.Children.Add(buttonRow);
        _saveButton = new BevelledButtonWidget
        {
            Text = "保存",
            FontScale = 0.7f,
            Size = new Vector2(180f, 46f),
            Margin = new Vector2(8f, 0f),
        };
        _cancelButton = new BevelledButtonWidget
        {
            Text = "取消",
            FontScale = 0.7f,
            Size = new Vector2(180f, 46f),
            Margin = new Vector2(8f, 0f),
        };
        buttonRow.Children.Add(_saveButton);
        buttonRow.Children.Add(_cancelButton);
    }

    public override void Update()
    {
        if (_closed)
        {
            return;
        }

        if (_saveButton.IsClicked)
        {
            var current = _component.Settings;
            _component.SaveSettings(new GeniusSettings
            {
                ApiBaseUrl = _urlBox.Text.Trim(),
                Model = _modelBox.Text.Trim(),
                ApiKey = _keyBox.Text.Trim(),
                SystemPromptExtra = current.SystemPromptExtra,
                MaxToolSteps = current.MaxToolSteps,
                RequestTimeoutSeconds = current.RequestTimeoutSeconds,
                ToolTimeoutSeconds = current.ToolTimeoutSeconds,
            });
            Close();
        }

        if (_cancelButton.IsClicked || Input.Cancel)
        {
            Close();
        }
    }

    private void Close()
    {
        if (!_closed)
        {
            _closed = true;
            DialogsManager.HideDialog(this);
        }
    }

    private static TextBoxWidget AddField(StackPanelWidget stack, string label, string value)
    {
        stack.Children.Add(new LabelWidget
        {
            Text = label,
            FontScale = 0.6f,
            Color = HintColor,
            Size = new Vector2(680f, 26f),
        });
        var box = new TextBoxWidget
        {
            Text = value,
            MaximumLength = 400,
            FontScale = 0.62f,
            Size = new Vector2(680f, 44f),
            Margin = new Vector2(0f, 2f),
        };
        stack.Children.Add(box);
        return box;
    }
}
