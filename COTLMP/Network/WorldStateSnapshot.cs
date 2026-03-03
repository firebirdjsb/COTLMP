/*
 * PROJECT:     Cult of the Lamb Multiplayer Mod
 * LICENSE:     MIT (https://spdx.org/licenses/MIT)
 * PURPOSE:     Serializable world-state snapshot for host-to-client sync
 * COPYRIGHT:   Copyright 2025 COTLMP Contributors
 */

/* IMPORTS ********************************************************************/

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

/* CLASSES & CODE *************************************************************/

namespace COTLMP.Network
{
    /**
     * @brief
     * Holds a point-in-time snapshot of the host's world state.
     * Designed for binary serialization followed by GZip compression
     * so it can be sent over the network as a single message payload.
     *
     * @field FormatVersion
     * Incremented when the binary layout changes (backwards-compat guard).
     */
    internal sealed class WorldStateSnapshot
    {
        public const int FormatVersion = 7;

        /* ------------------------------------------------------------------ */
        /* Header                                                               */
        /* ------------------------------------------------------------------ */

        public string SceneName = "";
        public float  TimeOfDay;
        public int    CurrentDay;

        /** RNG seed used by the host for dungeon generation so clients
         *  produce identical room layouts. */
        public int    DungeonSeed;

        /* ------------------------------------------------------------------ */
        /* Dungeon room tracking (host-authoritative)                            */
        /* ------------------------------------------------------------------ */

        /** BiomeGenerator.Instance.CurrentX / CurrentY on the host.
         *  The client follows the host through rooms using these. */
        public int DungeonRoomX;
        public int DungeonRoomY = -1;

        /** GameManager.CurrentDungeonFloor on the host. */
        public int DungeonFloor;

        /** GameManager.CurrentDungeonLayer on the host. */
        public int DungeonLayer;

        /* ------------------------------------------------------------------ */
        /* Weather (host-authoritative)                                          */
        /* ------------------------------------------------------------------ */

        public int   WeatherType;         // WeatherSystemController.WeatherType
        public int   WeatherStrength;     // WeatherSystemController.WeatherStrength
        public int   WeatherDuration;
        public float WeatherStartTime;

        /* ------------------------------------------------------------------ */
        /* Cult stats (host-authoritative)                                       */
        /* ------------------------------------------------------------------ */

        public float CultFaith;
        public float StaticFaith;
        public float HungerBar;
        public float IllnessBar;
        public float IllnessBarMax;

        /* ------------------------------------------------------------------ */
        /* Player equipment (host-authoritative)                                */
        /* ------------------------------------------------------------------ */

        /** Weapon type the host is currently wielding (cast of EquipmentType). */
        public int EquippedWeaponType = -1;

        /** Curse type the host is currently using (cast of EquipmentType). */
        public int EquippedCurseType  = -1;

        public RelicEntry[] Relics = Array.Empty<RelicEntry>();

        /* ------------------------------------------------------------------ */
        /* Sections                                                             */
        /* ------------------------------------------------------------------ */

        public FollowerEntry[]    Followers    = Array.Empty<FollowerEntry>();
        public StructureEntry[]   Structures   = Array.Empty<StructureEntry>();
        public EnemyEntry[]       Enemies      = Array.Empty<EnemyEntry>();
        public DroppedItemEntry[] DroppedItems = Array.Empty<DroppedItemEntry>();
        public ResourceEntry[]    Resources    = Array.Empty<ResourceEntry>();
        public NpcEntry[]         Npcs         = Array.Empty<NpcEntry>();
        public CritterEntry[]     Critters     = Array.Empty<CritterEntry>();

        /* ------------------------------------------------------------------ */
        /* Inner data types                                                     */
        /* ------------------------------------------------------------------ */

        public struct FollowerEntry
        {
            public int    ID;
            public float  X, Y;
            public int    TaskType;
            public int    Role;
            public string Name;
            public string Animation;
            public float  FacingAngle;
            public float  SpineScaleX;
        }

        public struct StructureEntry
        {
            public int   ID;
            public int   Type;
            public float X, Y;
            public bool  IsCollapsed;
            public bool  IsAflame;
        }

        public struct EnemyEntry
        {
            public int    InstanceID;
            public string TypeName;
            public float  X, Y;
            public float  HP, TotalHP;
            public bool   IsDead;
        }

        public struct DroppedItemEntry
        {
            public int   InstanceID;
            public int   Type;
            public int   Quantity;
            public float X, Y;
        }

        public struct ResourceEntry
        {
            public int    InstanceID;
            public string TypeName;
            public float  X, Y;
            public float  HP, TotalHP;
            public bool   IsDestroyed;
        }

        /** Insects, animals, and ambient NPCs that roam the scene. */
        public struct NpcEntry
        {
            public int    InstanceID;
            public string TypeName;
            public float  X, Y;
            public float  HP, TotalHP;
            public bool   IsDead;
        }

        /** Critters: spiders, birds, bees, butterflies, etc. */
        public struct CritterEntry
        {
            public int    InstanceID;
            public string TypeName;
            public float  X, Y;
            public bool   IsDead;
        }

        /** Relics / trinkets held during a dungeon run. */
        public struct RelicEntry
        {
            public int  Type;       // TarotCards.Card cast to int
            public int  Level;
        }

        /* ------------------------------------------------------------------ */
        /* Binary serialization                                                 */
        /* ------------------------------------------------------------------ */

        /**
         * @brief
         * Writes the snapshot into a raw byte array.
         *
         * @return
         * The serialized bytes (not yet compressed).
         */
        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w  = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(FormatVersion);
                WriteStr(w, SceneName);
                w.Write(TimeOfDay);
                w.Write(CurrentDay);
                w.Write(DungeonSeed);
                w.Write(DungeonRoomX);
                w.Write(DungeonRoomY);
                w.Write(DungeonFloor);
                w.Write(DungeonLayer);

                /* Weather */
                w.Write(WeatherType);
                w.Write(WeatherStrength);
                w.Write(WeatherDuration);
                w.Write(WeatherStartTime);

                /* Cult stats */
                w.Write(CultFaith);
                w.Write(StaticFaith);
                w.Write(HungerBar);
                w.Write(IllnessBar);
                w.Write(IllnessBarMax);

                /* Equipment */
                w.Write(EquippedWeaponType);
                w.Write(EquippedCurseType);
                w.Write(Relics.Length);
                foreach (var rel in Relics)
                {
                    w.Write(rel.Type);
                    w.Write(rel.Level);
                }

                /* Followers */
                w.Write(Followers.Length);
                foreach (var f in Followers)
                {
                    w.Write(f.ID);
                    w.Write(f.X); w.Write(f.Y);
                    w.Write(f.TaskType);
                    w.Write(f.Role);
                    WriteStr(w, f.Name);
                    WriteStr(w, f.Animation);
                    w.Write(f.FacingAngle);
                    w.Write(f.SpineScaleX);
                }

                /* Structures */
                w.Write(Structures.Length);
                foreach (var s in Structures)
                {
                    w.Write(s.ID); w.Write(s.Type);
                    w.Write(s.X); w.Write(s.Y);
                    w.Write(s.IsCollapsed); w.Write(s.IsAflame);
                }

                /* Enemies */
                w.Write(Enemies.Length);
                foreach (var e in Enemies)
                {
                    w.Write(e.InstanceID);
                    WriteStr(w, e.TypeName);
                    w.Write(e.X); w.Write(e.Y);
                    w.Write(e.HP); w.Write(e.TotalHP);
                    w.Write(e.IsDead);
                }

                /* Dropped items */
                w.Write(DroppedItems.Length);
                foreach (var i in DroppedItems)
                {
                    w.Write(i.InstanceID); w.Write(i.Type);
                    w.Write(i.Quantity);
                    w.Write(i.X); w.Write(i.Y);
                }

                /* Resources */
                w.Write(Resources.Length);
                foreach (var r in Resources)
                {
                    w.Write(r.InstanceID);
                    WriteStr(w, r.TypeName);
                    w.Write(r.X); w.Write(r.Y);
                    w.Write(r.HP); w.Write(r.TotalHP);
                    w.Write(r.IsDestroyed);
                }

                /* NPCs (insects, animals, ambient creatures) */
                w.Write(Npcs.Length);
                foreach (var n in Npcs)
                {
                    w.Write(n.InstanceID);
                    WriteStr(w, n.TypeName);
                    w.Write(n.X); w.Write(n.Y);
                    w.Write(n.HP); w.Write(n.TotalHP);
                    w.Write(n.IsDead);
                }

                /* Critters (spiders, birds, bees, etc.) */
                w.Write(Critters.Length);
                foreach (var c in Critters)
                {
                    w.Write(c.InstanceID);
                    WriteStr(w, c.TypeName);
                    w.Write(c.X); w.Write(c.Y);
                    w.Write(c.IsDead);
                }

                return ms.ToArray();
            }
        }

        /**
         * @brief
         * Reads a snapshot from a raw byte array.
         *
         * @param[in] data
         * The serialized bytes (not compressed).
         *
         * @return
         * The deserialized WorldStateSnapshot.
         */
        public static WorldStateSnapshot Deserialize(byte[] data)
        {
            var snap = new WorldStateSnapshot();
            using (var ms = new MemoryStream(data))
            using (var r  = new BinaryReader(ms, Encoding.UTF8, true))
            {
                int version = r.ReadInt32();
                if (version != FormatVersion)
                    throw new InvalidDataException($"Unsupported snapshot version {version}");

                snap.SceneName  = ReadStr(r);
                snap.TimeOfDay  = r.ReadSingle();
                snap.CurrentDay = r.ReadInt32();
                snap.DungeonSeed = r.ReadInt32();
                snap.DungeonRoomX = r.ReadInt32();
                snap.DungeonRoomY = r.ReadInt32();
                snap.DungeonFloor = r.ReadInt32();
                snap.DungeonLayer = r.ReadInt32();

                /* Weather */
                snap.WeatherType     = r.ReadInt32();
                snap.WeatherStrength = r.ReadInt32();
                snap.WeatherDuration = r.ReadInt32();
                snap.WeatherStartTime = r.ReadSingle();

                /* Cult stats */
                snap.CultFaith    = r.ReadSingle();
                snap.StaticFaith  = r.ReadSingle();
                snap.HungerBar    = r.ReadSingle();
                snap.IllnessBar   = r.ReadSingle();
                snap.IllnessBarMax = r.ReadSingle();

                /* Equipment */
                snap.EquippedWeaponType = r.ReadInt32();
                snap.EquippedCurseType  = r.ReadInt32();
                int relicCount = r.ReadInt32();
                snap.Relics = new RelicEntry[relicCount];
                for (int i = 0; i < relicCount; i++)
                {
                    snap.Relics[i] = new RelicEntry
                    {
                        Type  = r.ReadInt32(),
                        Level = r.ReadInt32()
                    };
                }

                /* Followers */
                int count = r.ReadInt32();
                snap.Followers = new FollowerEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.Followers[i] = new FollowerEntry
                    {
                        ID          = r.ReadInt32(),
                        X           = r.ReadSingle(), Y = r.ReadSingle(),
                        TaskType    = r.ReadInt32(),
                        Role        = r.ReadInt32(),
                        Name        = ReadStr(r),
                        Animation   = ReadStr(r),
                        FacingAngle = r.ReadSingle(),
                        SpineScaleX = r.ReadSingle()
                    };
                }

                /* Structures */
                count = r.ReadInt32();
                snap.Structures = new StructureEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.Structures[i] = new StructureEntry
                    {
                        ID          = r.ReadInt32(), Type = r.ReadInt32(),
                        X           = r.ReadSingle(), Y = r.ReadSingle(),
                        IsCollapsed = r.ReadBoolean(), IsAflame = r.ReadBoolean()
                    };
                }

                /* Enemies */
                count = r.ReadInt32();
                snap.Enemies = new EnemyEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.Enemies[i] = new EnemyEntry
                    {
                        InstanceID = r.ReadInt32(),
                        TypeName   = ReadStr(r),
                        X          = r.ReadSingle(), Y = r.ReadSingle(),
                        HP         = r.ReadSingle(), TotalHP = r.ReadSingle(),
                        IsDead     = r.ReadBoolean()
                    };
                }

                /* Dropped items */
                count = r.ReadInt32();
                snap.DroppedItems = new DroppedItemEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.DroppedItems[i] = new DroppedItemEntry
                    {
                        InstanceID = r.ReadInt32(), Type = r.ReadInt32(),
                        Quantity   = r.ReadInt32(),
                        X          = r.ReadSingle(), Y = r.ReadSingle()
                    };
                }

                /* Resources */
                count = r.ReadInt32();
                snap.Resources = new ResourceEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.Resources[i] = new ResourceEntry
                    {
                        InstanceID  = r.ReadInt32(),
                        TypeName    = ReadStr(r),
                        X           = r.ReadSingle(), Y = r.ReadSingle(),
                        HP          = r.ReadSingle(), TotalHP = r.ReadSingle(),
                        IsDestroyed = r.ReadBoolean()
                    };
                }

                /* NPCs */
                count = r.ReadInt32();
                snap.Npcs = new NpcEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.Npcs[i] = new NpcEntry
                    {
                        InstanceID = r.ReadInt32(),
                        TypeName   = ReadStr(r),
                        X          = r.ReadSingle(), Y = r.ReadSingle(),
                        HP         = r.ReadSingle(), TotalHP = r.ReadSingle(),
                        IsDead     = r.ReadBoolean()
                    };
                }

                /* Critters */
                count = r.ReadInt32();
                snap.Critters = new CritterEntry[count];
                for (int i = 0; i < count; i++)
                {
                    snap.Critters[i] = new CritterEntry
                    {
                        InstanceID = r.ReadInt32(),
                        TypeName   = ReadStr(r),
                        X          = r.ReadSingle(), Y = r.ReadSingle(),
                        IsDead     = r.ReadBoolean()
                    };
                }
            }
            return snap;
        }

        /* ------------------------------------------------------------------ */
        /* GZip compression                                                     */
        /* ------------------------------------------------------------------ */

        /**
         * @brief
         * Compresses raw bytes using GZip.
         */
        public static byte[] Compress(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionMode.Compress, true))
                    gz.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }

        /**
         * @brief
         * Decompresses GZip bytes back to raw.
         */
        public static byte[] Decompress(byte[] compressed)
        {
            using (var input  = new MemoryStream(compressed))
            using (var gz     = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return output.ToArray();
            }
        }

        /* ------------------------------------------------------------------ */
        /* String helpers                                                       */
        /* ------------------------------------------------------------------ */

        private static void WriteStr(BinaryWriter w, string s)
        {
            if (string.IsNullOrEmpty(s)) { w.Write(0); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadInt32();
            return len <= 0 ? string.Empty : Encoding.UTF8.GetString(r.ReadBytes(len));
        }
    }
}

/* EOF */
