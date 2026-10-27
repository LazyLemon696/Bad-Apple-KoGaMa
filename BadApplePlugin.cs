using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using OpenKogama.Api;

namespace BadApple;
public sealed class BadApplePlugin : IPlugin
{
    const string CommandName = "badapple";
    static readonly Vector3 CornerA = new(-11f, 18f, -46.5f);
    static readonly Vector3 CornerB = new(19f, 2f, -46.5f);
    static readonly Material[] Shades =
    [
        Material.BlackConcrete, Material.DarkConcrete, Material.Concrete, Material.LightConcrete, Material.Cloud,
    ];
    const int EmitterRange = 2;
    const float EmitterVolume = 1f;
    const string SoundName = "Bad Apple";
    const long VideoDelayMs = 500;
    const long TailMs = 3_000;
    const long SoundMs = 219_190;
    const long TestMs = 30_000;

    const string Usage = "Type !badapple to play it. Admins also have !badapple stop, test, flip and z";
    const string Gold = "#ffd166";
    const string Warn = "#ff8c5a";
    const string FlipKey = "flip";
    const string ZKey = "z";

    static readonly byte[] Levels = BuildLevels();

    static byte[] BuildLevels()
    {
        byte[] levels = new byte[16];
        for (int step = 0; step < levels.Length; step++)
            levels[step] = (byte)((step * (Shades.Length - 1) * 2 + 15) / 30);
        return levels;
    }

    static readonly Vector3 Center = (CornerA + CornerB) / 2f;

    sealed record Screen(int Left, int Right, int Bottom, int Top, int Z, bool Flip)
    {
        public int Width => Right - Left + 1;
        public int Height => Top - Bottom + 1;
        public int Cells => Width * Height;

        public (int X, int Y, int Z) At(int cell)
        {
            int column = cell % Width;
            int row = cell / Width;
            return (Flip ? Right - column : Left + column, Top - row, Z);
        }
    }

    sealed class Show(int worldId, Screen screen, Cube?[] original, int[] source, bool test)
    {
        public int WorldId => worldId;
        public Screen Screen => screen;
        public Cube?[] Original => original;
        public int[] Source => source;
        public bool Test => test;
        public byte[]? Shown { get; set; }
        public int Frame { get; set; } = -1;
        public int? Emitter { get; set; }
        public long VideoStart { get; set; }
        public long EndAt { get; set; }
    }

    sealed class Frames(int width, int height, int[] starts, int totalMs, byte[] cells)
    {
        public int Width => width;
        public int Height => height;
        public int Count => starts.Length;
        public int[] Starts => starts;
        public int TotalMs => totalMs;
        public byte[] Cells => cells;

        public static Frames Load(Stream file)
        {
            using var unzipped = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            unzipped.CopyTo(buffer);
            buffer.Position = 0;

            using var reader = new BinaryReader(buffer);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "BAF1") throw new InvalidDataException("badapple.frames is not a frames file");
            int w = reader.ReadUInt16();
            int h = reader.ReadUInt16();
            int count = reader.ReadInt32();
            int ms = reader.ReadInt32();
            int[] begins = new int[count];
            for (int i = 0; i < count; i++) begins[i] = reader.ReadInt32();
            int size = checked(count * w * h);
            byte[] pixels = reader.ReadBytes(size);
            if (pixels.Length != size || count == 0) throw new InvalidDataException("badapple.frames is cut short");
            return new Frames(w, h, begins, ms, pixels);
        }
    }

    readonly object _sync = new();
    readonly Dictionary<int, Show> _shows = [];
    IServer _server = null!;
    IPluginStorage _store = null!;
    Frames _frames = null!;
    ISound? _sound;
    bool _flip;
    int? _z;

    public void Load(IServer server)
    {
        _server = server;
        _store = server.Storage("badapple");
        _flip = _store.Get(FlipKey) == "1";
        _z = int.TryParse(_store.Get(ZKey), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z) ? z : null;

        _frames = Frames.Load(Resource("badapple.frames"));
        _sound = server.Assets.Sounds.Add(SoundName, ReadAll(Resource("badapple.mp3")), SoundGroup.Music);

        server.AddCommand(CommandName, Command);
        server.Update += Update;
        server.PlayerLeft += Left;
        server.Log($"badapple: {_frames.Count} frames of {_frames.Width}x{_frames.Height}, type !{CommandName} to play");
    }

    public void Unload()
    {
        lock (_sync)
        {
            _server.Update -= Update;
            _server.PlayerLeft -= Left;
            foreach (Show show in _shows.Values.ToList())
                Finish(WorldOf(show), show, null);
            _shows.Clear();
        }
    }

    static Stream Resource(string name) =>
        typeof(BadApplePlugin).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException($"{name} is not embedded in the plugin");

    static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    IWorld? WorldOf(Show show) => _server.Worlds.FirstOrDefault(world => world.Id == show.WorldId);

    void Command(IPlayer player, string[] arguments)
    {
        lock (_sync)
        {
            string action = arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "";
            if (action == "")
            {
                Play(player);
                return;
            }
            if (action is not ("stop" or "test" or "flip" or "z"))
            {
                player.Message(Usage);
                return;
            }
            if (!player.IsAdmin)
            {
                player.Message("Only admins can do that", Warn);
                return;
            }
            switch (action)
            {
                case "stop": Stop(player); break;
                case "test": Test(player); break;
                case "flip": Flip(player); break;
                default: Layer(player, arguments); break;
            }
        }
    }

    void Play(IPlayer player)
    {
        IWorld world = player.World;
        if (_sound is null)
        {
            player.Message("The Bad Apple sound could not be loaded, see the server log", Warn);
            return;
        }
        if (_shows.TryGetValue(world.Id, out Show? running))
        {
            player.Message(running.Test ? "The screen is being tested, try again in a moment" : "Bad Apple is already playing, wait until it is over", Warn);
            return;
        }
        if (Begin(world, false, out string error) is null)
        {
            player.Message(error, Warn);
            return;
        }
        world.Broadcast($"{player.Name} started Bad Apple!!", Gold);
    }

    void Stop(IPlayer player)
    {
        if (!_shows.TryGetValue(player.World.Id, out Show? show))
        {
            player.Message("Nothing is playing");
            return;
        }
        Finish(player.World, show, "Bad Apple!! was stopped");
    }

    void Test(IPlayer player)
    {
        IWorld world = player.World;
        if (_shows.ContainsKey(world.Id))
        {
            player.Message("The screen is busy, use !badapple stop first", Warn);
            return;
        }
        if (Begin(world, true, out string error) is null)
        {
            player.Message(error, Warn);
            return;
        }
        player.Message("Test pattern for 30 s: a bright block must sit in the top left corner of the screen as you see it, "
            + "and the bottom row must go from dark on the left to bright on the right. If the picture is mirrored, "
            + "type !badapple flip. If the screen is a cube in front of or behind your wall, type !badapple z <number>.");
    }

    void Flip(IPlayer player)
    {
        _flip = !_flip;
        _store.Set(FlipKey, _flip ? "1" : "0");
        player.Message(_flip ? "The picture is flipped now" : "The picture is not flipped now");
        player.Message("Run !badapple test to check it");
    }

    void Layer(IPlayer player, string[] arguments)
    {
        if (arguments.Length > 1)
        {
            if (arguments[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                _z = null;
                _store.Set(ZKey, null);
            }
            else if (int.TryParse(arguments[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z))
            {
                _z = z;
                _store.Set(ZKey, z.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                player.Message("Use !badapple z <number> or !badapple z auto");
                return;
            }
        }
        try
        {
            Screen screen = ScreenIn(player.World);
            player.Message($"The screen is {screen.Width} x {screen.Height} cubes, x {screen.Left}..{screen.Right}, y {screen.Bottom}..{screen.Top}, "
                + $"z layer {screen.Z}{(_z is null ? " (automatic)" : "")}");
        }
        catch (InvalidOperationException)
        {
            player.Message("This world has no terrain", Warn);
        }
    }

    void Left(IPlayer player)
    {
        lock (_sync)
        {
            IWorld world = player.World;
            if (_shows.TryGetValue(world.Id, out Show? show) && world.Players.All(other => other.Actor == player.Actor))
                Finish(world, show, null);
        }
    }

    Screen ScreenIn(IWorld world)
    {
        ITerrain terrain = world.Terrain;
        (int ax, int ay, int az) = terrain.CubeAt(CornerA);
        (int bx, int by, _) = terrain.CubeAt(CornerB);
        return new Screen(Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by), _z ?? az, _flip);
    }

    Show? Begin(IWorld world, bool test, out string error)
    {
        error = "";
        Screen screen;
        Cube?[] original;
        try
        {
            screen = ScreenIn(world);
            if (screen.Cells > 10_000)
            {
                error = $"The screen would be {screen.Width} x {screen.Height} cubes, check the corners in the plugin";
                return null;
            }
            original = new Cube?[screen.Cells];
            for (int cell = 0; cell < original.Length; cell++)
            {
                (int x, int y, int z) = screen.At(cell);
                original[cell] = world.Terrain.Get(x, y, z);
            }
        }
        catch (InvalidOperationException)
        {
            error = "This world has no terrain to draw the screen on";
            return null;
        }

        var show = new Show(world.Id, screen, original, SourceMap(screen), test);
        _shows[world.Id] = show;
        try
        {
            long now = Environment.TickCount64;
            if (test)
            {
                show.EndAt = now + TestMs;
                Paint(world, show, TestPattern(screen));
                return show;
            }

            IWorldObject emitter = world.Add("SoundEmitter", Center, new Dictionary<string, object>
            {
                ["range"] = EmitterRange,
                ["pitch"] = 1f,
                ["mute"] = false,
                ["loop"] = false,
                ["volume"] = EmitterVolume,
            });
            show.Emitter = emitter.Id;
            if (!world.SetSound(emitter.Id, _sound!)) throw new InvalidOperationException("the sound is no longer registered");

            show.VideoStart = now + VideoDelayMs;
            show.EndAt = show.VideoStart + Math.Max(_frames.TotalMs, SoundMs) + TailMs;
            Draw(world, show, 0);
            return show;
        }
        catch (Exception problem)
        {
            _server.Log($"badapple: could not start: {problem.Message}");
            Finish(world, show, null);
            error = "Bad Apple could not start, see the server log";
            return null;
        }
    }

    int[] SourceMap(Screen screen)
    {
        int[] map = new int[screen.Cells];
        for (int row = 0; row < screen.Height; row++)
            for (int column = 0; column < screen.Width; column++)
                map[row * screen.Width + column] = row * _frames.Height / screen.Height * _frames.Width + column * _frames.Width / screen.Width;
        return map;
    }

    void Update(float seconds)
    {
        lock (_sync)
        {
            if (_shows.Count == 0) return;
            long now = Environment.TickCount64;
            foreach (Show show in _shows.Values.ToList())
            {
                try
                {
                    IWorld? world = WorldOf(show);
                    if (world is null)
                    {
                        _shows.Remove(show.WorldId);
                        continue;
                    }
                    if (now >= show.EndAt)
                    {
                        Finish(world, show, show.Test ? null : "Bad Apple!! is over, type !badapple to watch it again");
                        continue;
                    }
                    if (show.Test) continue;

                    int frame = FrameAt(now - show.VideoStart);
                    if (frame != show.Frame) Draw(world, show, frame);
                }
                catch (Exception problem)
                {
                    _server.Log($"badapple: stopped, {problem.Message}");
                    Finish(WorldOf(show), show, null);
                }
            }
        }
    }

    int FrameAt(long elapsed)
    {
        if (elapsed <= 0) return 0;
        int at = Array.BinarySearch(_frames.Starts, (int)Math.Min(elapsed, int.MaxValue));
        if (at < 0) at = ~at - 1;
        return Math.Clamp(at, 0, _frames.Count - 1);
    }

    void Draw(IWorld world, Show show, int frame)
    {
        byte[] target = new byte[show.Screen.Cells];
        int offset = frame * _frames.Width * _frames.Height;
        for (int cell = 0; cell < target.Length; cell++)
            target[cell] = Levels[_frames.Cells[offset + show.Source[cell]]];
        Paint(world, show, target);
        show.Frame = frame;
    }

    void Paint(IWorld world, Show show, byte[] target)
    {
        using ICubeEdit edit = world.Terrain.Edit();
        for (int cell = 0; cell < target.Length; cell++)
        {
            if (show.Shown is { } shown && shown[cell] == target[cell]) continue;
            (int x, int y, int z) = show.Screen.At(cell);
            edit.Set(x, y, z, Shades[target[cell]]);
        }
        show.Shown = target;
    }

    void Finish(IWorld? world, Show show, string? announcement)
    {
        _shows.Remove(show.WorldId);
        if (world is null) return;
        try
        {
            if (show.Emitter is int emitter) world.Remove(emitter);
            if (show.Shown is not null) Restore(world, show);
            if (announcement is not null) world.Broadcast(announcement, Gold);
        }
        catch (Exception problem)
        {
            _server.Log($"badapple: could not clean up: {problem.Message}");
        }
    }

    void Restore(IWorld world, Show show)
    {
        using ICubeEdit edit = world.Terrain.Edit();
        for (int cell = 0; cell < show.Original.Length; cell++)
        {
            (int x, int y, int z) = show.Screen.At(cell);
            if (show.Original[cell] is { } cube) edit.Set(x, y, z, cube);
            else edit.Remove(x, y, z);
        }
    }

    byte[] TestPattern(Screen screen)
    {
        byte bright = (byte)(Shades.Length - 1);
        byte[] cells = new byte[screen.Cells];
        for (int row = 0; row < screen.Height; row++)
            for (int column = 0; column < screen.Width; column++)
            {
                bool border = row == 0 || column == 0 || row == screen.Height - 1 || column == screen.Width - 1;
                bool corner = row < 4 && column < 8;
                bool ramp = row == screen.Height - 2 && column > 0 && column < screen.Width - 1;
                cells[row * screen.Width + column] = border || corner ? bright : ramp ? (byte)(column * Shades.Length / screen.Width) : (byte)0;
            }
        return cells;
    }
}
