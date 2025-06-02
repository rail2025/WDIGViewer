// Configuration.cs
using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Textures.TextureWraps;

namespace WDIGViewer
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 6;
        public bool IsConfigWindowMovable { get; set; } = true;
        public string UserImageDirectory { get; set; } = string.Empty;

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public Configuration() { }

        public void Initialize(IDalamudPluginInterface pInterface)
        {
            this.pluginInterface = pInterface;
        }

        public void Save()
        {
            this.pluginInterface?.SavePluginConfig(this);
        }
    }

    public enum ImageSourceType
    {
        Plugin,
        User,
        Internet
    }

    public class ImageAsset : IDisposable
    {
        public string FilePath { get; set; }
        public IDalamudTextureWrap? TextureWrap { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        private bool disposedValue;

        public ImageAsset(string filePath) { FilePath = filePath; }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    TextureWrap?.Dispose();
                    TextureWrap = null;
                }
                disposedValue = true;
            }
        }
        public void Dispose() { Dispose(disposing: true); GC.SuppressFinalize(this); }
    }

    public class FightPhase : IDisposable
    {
        public string Name { get; set; }
        public List<ImageAsset> Images { get; set; } = new();
        public int CurrentImageIndex { get; set; } = 0;
        private bool disposedValue;

        public FightPhase(string name) { Name = name; }

        public void LoadImages(Func<string, IDalamudTextureWrap?> textureLoader)
        {
            foreach (var imageAsset in Images)
            {
                imageAsset.TextureWrap?.Dispose();
                imageAsset.TextureWrap = null;
                imageAsset.Width = 0;
                imageAsset.Height = 0;

                if (!string.IsNullOrEmpty(imageAsset.FilePath) && System.IO.File.Exists(imageAsset.FilePath))
                {
                    imageAsset.TextureWrap = textureLoader(imageAsset.FilePath);
                    if (imageAsset.TextureWrap != null)
                    {
                        imageAsset.Width = imageAsset.TextureWrap.Width;
                        imageAsset.Height = imageAsset.TextureWrap.Height;
                    }
                }
            }
        }
        public ImageAsset? GetCurrentImage()
        {
            if (Images.Count > 0 && CurrentImageIndex >= 0 && CurrentImageIndex < Images.Count) return Images[CurrentImageIndex];
            return null;
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue) { if (disposing) { foreach (var imageAsset in Images) imageAsset?.Dispose(); Images.Clear(); } disposedValue = true; }
        }
        public void Dispose() { Dispose(disposing: true); GC.SuppressFinalize(this); }
    }

    public class FightStrategy : IDisposable
    {
        public string Name { get; set; }
        public ImageSourceType Source { get; set; }
        public List<FightPhase> Phases { get; set; } = new();
        public string RootPath { get; set; }
        public ushort? MetadataTerritoryTypeId { get; set; } // ADDED: For metadata-based Territory ID matching
        private bool disposedValue;

        public FightStrategy(string name, ImageSourceType source, string rootPath = "")
        {
            Name = name;
            Source = source;
            RootPath = rootPath;
            MetadataTerritoryTypeId = null; // Initialize
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue) { if (disposing) { foreach (var phase in Phases) phase?.Dispose(); Phases.Clear(); } disposedValue = true; }
        }
        public void Dispose() { Dispose(disposing: true); GC.SuppressFinalize(this); }
    }

}
