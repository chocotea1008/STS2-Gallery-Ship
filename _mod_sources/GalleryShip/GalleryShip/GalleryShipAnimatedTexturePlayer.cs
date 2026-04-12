using Godot;

namespace GalleryShip;

internal sealed class GalleryShipAnimatedTexturePlayer : Node
{
	private const string PlayerNodeName = "__GalleryShipAnimatedTexturePlayer";

	private TextureRect? _target;
	private GalleryShipTextureAsset? _asset;
	private int _frameIndex;
	private ulong _bindVersion;
	private Timer? _frameTimer;

	internal static void Apply(TextureRect target, GalleryShipTextureAsset asset)
	{
		GalleryShipAnimatedTexturePlayer player = GetOrCreate(target);
		player.Bind(target, asset);
	}

	internal static void Clear(TextureRect target)
	{
		if (target.GetNodeOrNull<GalleryShipAnimatedTexturePlayer>(PlayerNodeName) is { } player)
		{
			player.Reset();
		}
	}

	private static GalleryShipAnimatedTexturePlayer GetOrCreate(TextureRect target)
	{
		if (target.GetNodeOrNull<GalleryShipAnimatedTexturePlayer>(PlayerNodeName) is { } existing)
		{
			existing.EnsureTimer();
			return existing;
		}

		GalleryShipAnimatedTexturePlayer player = new()
		{
			Name = PlayerNodeName,
			ProcessMode = ProcessModeEnum.Always
		};
		target.AddChild(player);
		player.EnsureTimer();
		return player;
	}

	private void EnsureTimer()
	{
		if (_frameTimer != null && GodotObject.IsInstanceValid(_frameTimer))
		{
			return;
		}

		_frameTimer = new Timer
		{
			Name = "__FrameTimer",
			OneShot = true,
			Autostart = false,
			IgnoreTimeScale = true,
			ProcessCallback = Timer.TimerProcessCallback.Idle,
			ProcessMode = ProcessModeEnum.Always
		};
		_frameTimer.Timeout += OnFrameTimerTimeout;
		AddChild(_frameTimer);
	}

	private void Bind(TextureRect target, GalleryShipTextureAsset asset)
	{
		EnsureTimer();
		_target = target;
		_asset = asset;
		_frameIndex = 0;
		_bindVersion++;
		_target.Texture = asset.Texture;
		ScheduleNextFrame();
	}

	private void Reset()
	{
		if (_frameTimer != null && GodotObject.IsInstanceValid(_frameTimer))
		{
			_frameTimer.Stop();
		}

		_target = null;
		_asset = null;
		_frameIndex = 0;
		_bindVersion++;
	}

	private void OnFrameTimerTimeout()
	{
		if (_target == null || !GodotObject.IsInstanceValid(_target) || _asset == null || !_asset.IsAnimated || _asset.Frames.Count == 0)
		{
			return;
		}

		_frameIndex = (_frameIndex + 1) % _asset.Frames.Count;
		_target.Texture = _asset.Frames[_frameIndex];
		ScheduleNextFrame();
	}

	private void ScheduleNextFrame()
	{
		if (_frameTimer == null || !GodotObject.IsInstanceValid(_frameTimer))
		{
			return;
		}

		_frameTimer.Stop();
		if (_target == null || !GodotObject.IsInstanceValid(_target) || _asset == null || !_asset.IsAnimated || _asset.FrameDurations.Count == 0)
		{
			return;
		}

		double duration = _asset.FrameDurations[Mathf.Clamp(_frameIndex, 0, _asset.FrameDurations.Count - 1)];
		_frameTimer.Start(duration < 0.05d ? 0.05d : duration);
	}
}
