using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Arcade.Gameplay;
using DG.Tweening;
using Newtonsoft.Json;
using Arcade.Compose.Dialog;
using Arcade.Compose.Editing;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using System.Globalization;
using Arcade.Compose.Command;
using UnityEngine.InputSystem;
using Arcade.Compose.Operation;
using Arcade.Compose.UI;

namespace Arcade.Compose
{
	[Serializable]
	public class ArcadePreference
	{
		public int AgreedUserAgreementVersion;
		public long ReadWhatsNewVersion;
		public string ScreenResolution = "1280x720";
		public bool Fullscreen = false;
		public int TargetFrameRate = -1;
		public int Velocity = 30;
		public uint UndoBufferSize = 200;
		public bool PlaybackSync = false;
		public bool Auto;
		public int GlobalAudioOffset = 0;
		public Arcade.Gameplay.Chart.ChartSortMode ChartSortMode;
	}

	public class ArcadeComposeManager : MonoBehaviour
	{
		public static ArcadeComposeManager Instance { get; private set; }
		public const float ModeSwitchDuration = 0.3f;
		public const Ease ToEditorModeEase = Ease.OutCubic;
		public const Ease ToPlayerModeEase = Ease.InCubic;
		private string BuildString
		{
			get
			{
				return $"{Application.version} Build Time {new DateTime(BuildTimestamp).ToString("yyyyMMddHHmmss")}";
			}
		}

		public static string ArcadePersistentFolder
		{
			get
			{
				if (!Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/Arcade"))
					Directory.CreateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/Arcade");
				return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/Arcade";
			}
		}
		public string PreferencesSavePath
		{
			get
			{
				return ArcadePersistentFolder + "/Preferences.json";
			}
		}

		public bool IsEditorMode { get; set; } = true;

		public Rect EditorModeGameplayCameraRect
		{
			get
			{
				float left = LeftBarView.rect.width;
				float right = RightBarView.rect.width;
				float top = TopBarView.rect.height;
				float bottom = BottomBarView.rect.height;
				float width = Bars.rect.width;
				float height = Bars.rect.height;
				float rectLeft = left / width;
				float rectTop = top / height;
				float rectWidth = 1 - (left + right) / width;
				float rectHeight = 1 - (top + bottom) / height;
				if (width * rectWidth < height * rectHeight / 0.75f)
				{
					float targetWidth = height * rectHeight / 0.75f / width;
					rectLeft -= (targetWidth - rectWidth) / 2f;
					rectWidth = targetWidth;
				}
				return new Rect(rectLeft, rectTop, rectWidth, rectHeight);
			}
		}
		public Camera GameplayCamera, EditorCamera;
		public ArcGameplayManager GameplayManager;
		[Header("Bar")]
		public RectTransform EditorCanvas;
		public RectTransform TopBar;
		public RectTransform BottomBar, LeftBar, RightBar, Bars;
		public RectTransform TopBarView, BottomBarView, LeftBarView, RightBarView;

		public long BuildTimestamp
		{
			get
			{
				return long.Parse(BuildTimestampText.text, CultureInfo.InvariantCulture);
			}
		}

		[Header("Pause")]
		public Button PauseButton;
		public Image PauseButtonImage;
		public Sprite PausePause, PausePlay, PausePausePressed, PausePlayPressed;
		[Header("Info")]
		public CanvasGroup InfoCanvasGroup;
		public Image TimingSliderHandle;
		public Sprite DefaultSliderSprite, GlowSliderSprite;
		[Header("Auto")]
		public Button AutoButton;
		[Header("Misc")]

		public UnityEvent OnPlay = new UnityEvent();
		public UnityEvent OnPause = new UnityEvent();
		public AdeNumberInputWithSlider VelocitySlider;

		public ArcadePreference ArcadePreference = new ArcadePreference();
		public Text Version;

		public Dropdown ResolutionDropdown;
		public Toggle FullscreenToggle;
		public Dropdown TargetFramerateDropdown;
		public InputField UndoBufferSizeInput;
		public Toggle PlaybackSyncToggle;
		public InputField GlobalAudioOffsetInput;

		public TextAsset ChangeLog;
		public TextAsset BuildTimestampText;

		private bool switchingMode = false;
		private int playShotTiming = 0;
		private bool shouldGoBackToPlayShotTiming = false;

		private void Awake()
		{
			Instance = this;
			Version.text = BuildString;
			CultureInfo.CurrentCulture = new CultureInfo("zh-Hans");
		}
		private void Start()
		{
			ArcGameplayManager.Instance.OnMusicFinished.AddListener(Pause);
			ResolutionDropdown.onValueChanged.AddListener((int value) =>
			{
				ArcadePreference.ScreenResolution = ResolutionDropdown.options[value].text;
				SetResolution(ArcadePreference.ScreenResolution, ArcadePreference.Fullscreen);
				SavePreferences();
			});
			FullscreenToggle.onValueChanged.AddListener((bool value) =>
			{
				ArcadePreference.Fullscreen = value;
				ResolutionDropdown.interactable = !value;
				SetResolution(ArcadePreference.ScreenResolution, ArcadePreference.Fullscreen);
				SavePreferences();
			});
			TargetFramerateDropdown.onValueChanged.AddListener((int value) =>
			{
				int fps;
				if (!int.TryParse(TargetFramerateDropdown.options[value].text, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
				{
					fps = -1;
				}
				SetTargetFramerate(fps);
				ArcadePreference.TargetFrameRate = fps;
				SavePreferences();
			});
			UndoBufferSizeInput.onEndEdit.AddListener(SetUndoBufferSize);
			GlobalAudioOffsetInput.onEndEdit.AddListener(SetGlobalAudioOffset);
			PlaybackSyncToggle.onValueChanged.AddListener((bool value) =>
			{
				ArcadePreference.PlaybackSync = value;
				ArcGameplayManager.Instance.EnablePlaybackSync = value;
				SavePreferences();
			});
			VelocitySlider.onValueEdited += OnVelocityEdited;
			LoadPreferences();
			SavePreferences();
			Pause();
		}
		private enum ScrollingOperation
		{
			ScrollForward,
			ScrollBackward,
			ScrollToNextBeat,
			ScrollToPreviousBeat,
			ScrollToNextMeasure,
			ScrollToPreviousMeasure,

		}

		private ScrollingOperation? currentScrollingOperation = null;
		private bool isFirstScrollInterval = true;
		private float timeFromLastScroll = float.PositiveInfinity;

		private bool CheckScrollingOperation(ScrollingOperation? operation, float firstInterval, float thenInterval)
		{
			timeFromLastScroll += Time.deltaTime;
			if (operation != currentScrollingOperation)
			{
				currentScrollingOperation = operation;
				isFirstScrollInterval = true;
				timeFromLastScroll = operation == null ? float.PositiveInfinity : 0f;
				return true;
			}
			else
			{
				if (operation == null)
				{
					timeFromLastScroll = 0f;
					return true;
				}
				else if (isFirstScrollInterval && timeFromLastScroll > firstInterval)
				{
					isFirstScrollInterval = false;
					timeFromLastScroll = 0f;
					return true;
				}
				else if (!isFirstScrollInterval && timeFromLastScroll > thenInterval)
				{
					isFirstScrollInterval = false;
					timeFromLastScroll = 0f;
					return true;
				}
			}
			return false;
		}

		private void CheckScroll()
		{
			int timing = GameplayManager.ChartTiming;

			if (AdeInputManager.Instance.Hotkeys.ScrollToStart.WasPressedThisFrame())
			{
				CheckScrollingOperation(null, 0f, 0f);
				timing = GameplayManager.AllBeginChartTiming;
			}
			else if (AdeInputManager.Instance.Hotkeys.ScrollToEnd.WasPressedThisFrame())
			{
				CheckScrollingOperation(null, 0f, 0f);
				timing = GameplayManager.AllEndChartTiming;
			}
			else if (AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.ScrollToNextMeasure))
			{
				if (CheckScrollingOperation(ScrollingOperation.ScrollToNextMeasure, 0.5f, 0.05f))
				{
					timing = AdeGridManager.Instance.AttachMeasureScroll(timing, 1);
				}
			}
			else if (AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.ScrollToPreviousMeasure))
			{
				if (CheckScrollingOperation(ScrollingOperation.ScrollToPreviousBeat, 0.5f, 0.05f))
				{
					timing = AdeGridManager.Instance.AttachMeasureScroll(timing, -1);
				}
			}
			else if (AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.ScrollToNextBeat))
			{
				if (CheckScrollingOperation(ScrollingOperation.ScrollToNextBeat, 0.5f, 0.05f))
				{
					timing = AdeGridManager.Instance.AttachBeatScroll(timing, 1);
				}
			}
			else if (AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.ScrollToPreviousBeat))
			{
				if (CheckScrollingOperation(ScrollingOperation.ScrollToPreviousBeat, 0.5f, 0.05f))
				{
					timing = AdeGridManager.Instance.AttachBeatScroll(timing, -1);
				}
			}
			else if (AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.ScrollForward))
			{
				if (CheckScrollingOperation(ScrollingOperation.ScrollForward, 0.05f, 0.05f))
				{
					timing = AdeGridManager.Instance.AttachScroll(timing, 1);
				}
			}
			else if (AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.ScrollBackward))
			{
				if (CheckScrollingOperation(ScrollingOperation.ScrollBackward, 0.05f, 0.05f))
				{
					timing = AdeGridManager.Instance.AttachScroll(timing, -1);
				}
			}
			else if (Mouse.current.scroll.ReadValue().y != 0 && AdeGameplayContentInputHandler.InputActive)
			{
				CheckScrollingOperation(null, 0f, 0f);
				timing = AdeGridManager.Instance.AttachScroll(timing, Mouse.current.scroll.ReadValue().y);
			}
			else
			{
				CheckScrollingOperation(null, 0f, 0f);
				return;
			}

			if (timing > GameplayManager.AllEndChartTiming) timing = GameplayManager.AllEndChartTiming;
			if (timing < GameplayManager.AllBeginChartTiming) timing = GameplayManager.AllBeginChartTiming;

			if (GameplayManager.ChartTiming != Mathf.RoundToInt(timing))
			{
				GameplayManager.ChartTiming = Mathf.RoundToInt(timing);
				GameplayManager.ResetJudge();
			}
		}

		private void Update()
		{
			CheckScroll();
			if (IsEditorMode)
			{
				bool playHolding = AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.PlayWhenHolding);
				bool previewHolding = AdeInputManager.Instance.CheckHotkeyActionPressing(AdeInputManager.Instance.Hotkeys.PreviewWhenHolding);
				bool holding = playHolding || previewHolding;
				if (previewHolding)
				{
					shouldGoBackToPlayShotTiming = true;
				}
				else if (playHolding)
				{
					shouldGoBackToPlayShotTiming = false;
				}
				if (holding && !GameplayManager.IsPlaying)
				{
					GameplayManager.Play();
					playShotTiming = GameplayManager.AudioTiming;
					//AdeToast.Instance.Show("松开空格暂停并倒回，按下Q仅暂停", "Release 'Space' pause and rollback);
				}
				if (!holding && GameplayManager.IsPlaying)
				{
					GameplayManager.Pause();
					if (shouldGoBackToPlayShotTiming)
					{
						GameplayManager.AudioTiming = playShotTiming;
					}
				}
			}
			if (AdeInputManager.Instance.CheckHotkeyActionPressed(AdeInputManager.Instance.Hotkeys.PlayOrPause))
			{
				if (IsEditorMode) Play();
				else Pause();
			}
		}
		private void OnEnable()
		{
			Application.logMessageReceived += OnLog;
		}
		private void OnDisable()
		{
			Application.logMessageReceived -= OnLog;
		}
		private void OnLog(string condition, string stackTrace, LogType type)
		{
			if (type != LogType.Exception) return;
			try
			{
				AdeBasicSingleDialogContent.Instance.Show(condition + "\n" + stackTrace, "异常", "确定");
			}
			catch
			{

			}
		}

		public void SetResolution(string resolution, bool fullscreen)
		{
			if (fullscreen)
			{
				Debug.Log($"[fullscreen]{Screen.currentResolution.width.ToString(CultureInfo.InvariantCulture)}x{Screen.currentResolution.height.ToString(CultureInfo.InvariantCulture)}");
				Screen.SetResolution(Screen.currentResolution.width, Screen.currentResolution.height, FullScreenMode.FullScreenWindow);
				return;
			}
			Debug.Log($"[dpi]{Screen.dpi}");
			float scaleRatio = Screen.dpi / 96f;
			if (scaleRatio <= 0.01f)
			{
				scaleRatio = 1f;
			}
			// here we do not check format of string
			string[] dimensions = resolution.Split('x');
			int width = Mathf.RoundToInt(int.Parse(dimensions[0], CultureInfo.InvariantCulture) * scaleRatio);
			int height = Mathf.RoundToInt(int.Parse(dimensions[1], CultureInfo.InvariantCulture) * scaleRatio);
			Screen.SetResolution(width, height, FullScreenMode.Windowed);
		}
		public void SetTargetFramerate(int fps)
		{
			Application.targetFrameRate = fps;
		}

		public void Play()
		{
			if (AdeProjectManager.Instance.CurrentProjectMetadata == null || !ArcGameplayManager.Instance.IsLoaded)
			{
				AdeToast.Instance.Show("请先加载谱面");
				return;
			}

			if (switchingMode) return;
			switchingMode = true;

			GameplayManager.Play();
			TopBar.DOAnchorPosY(TopBar.sizeDelta.y, ModeSwitchDuration).SetEase(ToPlayerModeEase);
			BottomBar.DOAnchorPosY(-BottomBar.sizeDelta.y, ModeSwitchDuration).SetEase(ToPlayerModeEase);
			LeftBar.DOAnchorPosX(-LeftBar.sizeDelta.x, ModeSwitchDuration).SetEase(ToPlayerModeEase);
			RightBar.DOAnchorPosX(RightBar.sizeDelta.x, ModeSwitchDuration).SetEase(ToPlayerModeEase).OnComplete(() => { switchingMode = false; });
			GameplayCamera.DORect(new Rect(0, 0, 1, 1), ModeSwitchDuration).SetEase(ToPlayerModeEase);

			PauseButtonImage.sprite = PausePause;
			PauseButton.spriteState = new SpriteState() { pressedSprite = PausePausePressed };
			InfoCanvasGroup.interactable = false;

			TimingSliderHandle.sprite = GlowSliderSprite;

			AdeOperationManager.Instance.CancelOngoingOperation();
			AdeClickToCreate.Instance.Mode = ClickToCreateMode.Idle;

			IsEditorMode = false;
		}
		public void Pause()
		{
			if (switchingMode) return;
			switchingMode = true;

			GameplayManager.Pause();
			TopBar.DOAnchorPosY(0, ModeSwitchDuration).SetEase(ToEditorModeEase);
			BottomBar.DOAnchorPosY(0, ModeSwitchDuration).SetEase(ToEditorModeEase);
			LeftBar.DOAnchorPosX(0, ModeSwitchDuration).SetEase(ToEditorModeEase);
			RightBar.DOAnchorPosX(0, ModeSwitchDuration).SetEase(ToEditorModeEase).OnComplete(() => { switchingMode = false; });
			GameplayCamera.DORect(EditorModeGameplayCameraRect, ModeSwitchDuration).SetEase(ToEditorModeEase);

			PauseButtonImage.sprite = PausePlay;
			PauseButton.spriteState = new SpriteState() { pressedSprite = PausePlayPressed };
			InfoCanvasGroup.interactable = true;

			TimingSliderHandle.sprite = DefaultSliderSprite;

			IsEditorMode = true;
		}

		public void OnPauseClicked()
		{
			ArcGameplayManager.Instance.ResetJudge();
			if (IsEditorMode) Play();
			else Pause();
		}
		public void OnAutoClicked()
		{
			ArcGameplayManager.Instance.Auto = !ArcGameplayManager.Instance.Auto;
			ArcGameplayManager.Instance.ResetJudge();
			AutoButton.image.color = ArcGameplayManager.Instance.Auto ? new Color(0.59f, 0.55f, 0.65f, 1f) : new Color(0.9f, 0.9f, 0.9f, 1);
		}
		public void OnShutdownClicked()
		{
			AdeProjectManager.Instance.SaveProject();
			Application.Quit();
		}

		public void SetUndoBufferSize(string x)
		{
			uint size = uint.Parse(x, CultureInfo.InvariantCulture);
			ArcadePreference.UndoBufferSize = size;
			SavePreferences();
			AdeCommandManager.Instance.SetBufferSize(size);
		}

		public void SetGlobalAudioOffset(string x)
		{
			int offset = int.Parse(x, CultureInfo.InvariantCulture);
			ArcadePreference.GlobalAudioOffset = offset;
			SavePreferences();
			ArcGameplayManager.Instance.GlobalAudioOffset = offset;
		}

		private void OnVelocityEdited(float value)
		{
			ArcTimingManager.Instance.SettingVelocity = Mathf.RoundToInt(value * 30);
		}


		public void LoadPreferences()
		{
			try
			{
				if (File.Exists(PreferencesSavePath))
				{
					PlayerPrefs.SetString("ArcadeComposeManagerPreference", File.ReadAllText(PreferencesSavePath));
					File.Delete(PreferencesSavePath);
				}
				ArcadePreference = JsonConvert.DeserializeObject<ArcadePreference>(PlayerPrefs.GetString("ArcadeComposeManagerPreference", ""));
				if (ArcadePreference == null) ArcadePreference = new ArcadePreference();
			}
			catch (Exception)
			{
				ArcadePreference = new ArcadePreference();
			}
			finally
			{
				if (ArcadePreference.Velocity < 30)
				{
					ArcadePreference.Velocity = 30;
				}
				if (ArcadePreference.Velocity > 195)
				{
					ArcadePreference.Velocity = 195;
				}
				ArcTimingManager.Instance.SettingVelocity = ArcadePreference.Velocity;
				ArcGameplayManager.Instance.Auto = ArcadePreference.Auto;
				AdeProjectManager.Instance.SaveMode.text = ArcadePreference.ChartSortMode == Gameplay.Chart.ChartSortMode.Timing ? "按时间" : "按类别";
				AutoButton.image.color = ArcGameplayManager.Instance.Auto ? new Color(0.59f, 0.55f, 0.65f, 1f) : new Color(0.9f, 0.9f, 0.9f, 1);
				if (ArcadePreference.ReadWhatsNewVersion < BuildTimestamp)
				{
					AdeBasicSingleDialogContent.Instance.Show(ChangeLog.text);
					ArcadePreference.ReadWhatsNewVersion = BuildTimestamp;
				}
				if (ArcadePreference.AgreedUserAgreementVersion < ArcadeUserAgreementDialogContent.CurrentUserAgreementVersion)
				{
					ArcadeUserAgreementDialogContent.Instance.OpenDialog();
				}
				UndoBufferSizeInput.SetTextWithoutNotify($"{ArcadePreference.UndoBufferSize.ToString(CultureInfo.InvariantCulture)}");
				AdeCommandManager.Instance.bufferSize = ArcadePreference.UndoBufferSize;
				GlobalAudioOffsetInput.SetTextWithoutNotify($"{ArcadePreference.GlobalAudioOffset.ToString(CultureInfo.InvariantCulture)}");
				ArcGameplayManager.Instance.GlobalAudioOffset = ArcadePreference.GlobalAudioOffset;
				bool resolutionHit = false;
				for (int i = 0; i < ResolutionDropdown.options.Count; i++)
				{
					Dropdown.OptionData options = ResolutionDropdown.options[i];
					if (options.text == ArcadePreference.ScreenResolution)
					{
						ResolutionDropdown.SetValueWithoutNotify(i);
						resolutionHit = true;
					}
				}
				if (!resolutionHit)
				{
					ArcadePreference.ScreenResolution = ResolutionDropdown.options[ResolutionDropdown.value].text;
				}
				ResolutionDropdown.interactable = !ArcadePreference.Fullscreen;
				FullscreenToggle.SetIsOnWithoutNotify(ArcadePreference.Fullscreen);
				Screen.fullScreen = ArcadePreference.Fullscreen;
				SetResolution(ArcadePreference.ScreenResolution, ArcadePreference.Fullscreen);
				bool targetFramerateHit = false;
				for (int i = 0; i < TargetFramerateDropdown.options.Count; i++)
				{
					Dropdown.OptionData options = TargetFramerateDropdown.options[i];
					if (options.text == ArcadePreference.TargetFrameRate.ToString(CultureInfo.InvariantCulture))
					{
						TargetFramerateDropdown.SetValueWithoutNotify(i);
						targetFramerateHit = true;
					}
				}
				if (!targetFramerateHit)
				{
					ArcadePreference.TargetFrameRate = -1;
				}
				SetTargetFramerate(ArcadePreference.TargetFrameRate);
				PlaybackSyncToggle.SetIsOnWithoutNotify(ArcadePreference.PlaybackSync);
				ArcGameplayManager.Instance.EnablePlaybackSync = ArcadePreference.PlaybackSync;
			}
		}
		public void SavePreferences()
		{
			ArcadePreference.Velocity = ArcTimingManager.Instance.SettingVelocity;
			ArcadePreference.Auto = ArcGameplayManager.Instance.Auto;
			PlayerPrefs.SetString("ArcadeComposeManagerPreference", JsonConvert.SerializeObject(ArcadePreference));
		}

		public void SetGlowSliderKnob(Sprite sprite)
		{
			if (TimingSliderHandle.sprite == GlowSliderSprite)
			{
				TimingSliderHandle.sprite = sprite;
			}
			GlowSliderSprite = sprite;
		}

		public void SetPauseSprite(Sprite sprite)
		{
			if (PauseButtonImage.sprite == PausePause)
			{
				PauseButtonImage.sprite = sprite;
			}
			PausePause = sprite;
		}
		public void SetPlaySprite(Sprite sprite)
		{
			if (PauseButtonImage.sprite == PausePlay)
			{
				PauseButtonImage.sprite = sprite;
			}
			PausePlay = sprite;
		}
		public void SetPausePressedSprite(Sprite sprite)
		{
			if (PauseButton.spriteState.pressedSprite == PausePausePressed)
			{
				PauseButton.spriteState = new SpriteState { pressedSprite = sprite };
			}
			PausePausePressed = sprite;
		}
		public void SetPlayPressedSprite(Sprite sprite)
		{
			if (PauseButton.spriteState.pressedSprite == PausePlayPressed)
			{
				PauseButton.spriteState = new SpriteState { pressedSprite = sprite };
			}
			PausePlayPressed = sprite;
		}
		private void OnApplicationQuit()
		{
			Debug.Log("saving main setting when exit...");
			SavePreferences();
			Debug.Log("saved main setting when exit");
		}
		public void OpenLogFile()
		{
			Util.Shell.FileBrowser.OpenExplorer(Application.consoleLogPath);
		}

		public void UpdateResolution()
		{
			if (GameplayCamera)
			{
				TopBar.DOComplete();
				BottomBar.DOComplete();
				LeftBar.DOComplete();
				RightBar.DOComplete();
				GameplayCamera.DOComplete();
				GameplayCamera.rect = IsEditorMode ? EditorModeGameplayCameraRect : new Rect(0, 0, 1, 1);
				ArcCameraManager.Instance.ResetCamera();
			}
		}
	}
}
