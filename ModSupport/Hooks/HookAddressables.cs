using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Mortal
{
    /// <summary>
    /// 把 mod 目录里的图片映射成 Addressables 可定位的资源。
    /// 只要 key（相对路径，如 Portraits/player_normal.png）在任一 mod 目录下存在文件，就给出一条 IResourceLocation。
    /// </summary>
    public class ModSpriteLocator : IResourceLocator
    {
        public const string LocatorIdName = "ModSpriteLocator";

        private readonly string[] modPaths;

        public ModSpriteLocator(string[] modPaths)
        {
            this.modPaths = modPaths ?? new string[0];
        }

        public string LocatorId
        {
            get { return LocatorIdName; }
        }

        public IEnumerable<object> Keys
        {
            get { return new object[0]; }
        }

        public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
        {
            locations = null;

            if (type != null && !typeof(Sprite).IsAssignableFrom(type))
                return false;

            string relative = key as string;
            if (string.IsNullOrEmpty(relative))
                return false;

            // 只接受相对路径形式的 key，避免误伤官方的 guid / 完整路径 / url
            if (relative.StartsWith("/") || relative.StartsWith("\\") || relative.Contains("://") || relative.Contains(".."))
                return false;

            foreach (var modPath in modPaths)
            {
                if (string.IsNullOrEmpty(modPath))
                    continue;

                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(modPath, relative));
                }
                catch (Exception)
                {
                    continue;
                }

                // 防止越出 mod 目录
                string root = Path.GetFullPath(modPath);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    root += Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!File.Exists(full))
                    continue;

                locations = new List<IResourceLocation>
                {
                    new ResourceLocationBase(relative, full, ModSpriteProvider.ProviderIdName, typeof(Sprite))
                };
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 负责把 InternalId（磁盘绝对路径）加载成 Sprite，交给 HookMods.LoadSprite 处理（png/jpg/gif 都支持）。
    /// </summary>
    public class ModSpriteProvider : IResourceProvider
    {
        public const string ProviderIdName = "ModSpriteProvider";

        private readonly Func<string, Sprite> load;

        public ModSpriteProvider(Func<string, Sprite> load)
        {
            this.load = load;
        }

        public string ProviderId
        {
            get { return ProviderIdName; }
        }

        public ProviderBehaviourFlags BehaviourFlags
        {
            get { return ProviderBehaviourFlags.None; }
        }

        public bool CanProvide(Type type, IResourceLocation location)
        {
            return typeof(Sprite).IsAssignableFrom(type)
                && location != null
                && location.ProviderId == ProviderIdName;
        }

        public Type GetDefaultType(IResourceLocation location)
        {
            return typeof(Sprite);
        }

        public void Provide(ProvideHandle provideHandle)
        {
            string path = provideHandle.Location != null ? provideHandle.Location.InternalId : null;
            Sprite sprite = null;

            try
            {
                if (!string.IsNullOrEmpty(path) && load != null)
                    sprite = load(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"ModSupport: 加载图片失败 {path}: {e.Message}");
            }

            if (sprite == null)
            {
                provideHandle.Complete<Sprite>(null, false, new Exception($"ModSpriteProvider: 无法加载 {path}"));
                return;
            }

            provideHandle.Complete(sprite, true, null);
        }

        public void Release(IResourceLocation location, object asset)
        {
            // 图片由 HookMods.cacheSprite 统一缓存，不释放
        }
    }

    /// <summary>
    /// 在不修改 Unity.Addressables.dll 的前提下启用换图能力。
    ///
    /// 旧方案依赖被改过的 dll 暴露的 Addressables.ModPaths / Addressables.LoadSprite；
    /// 官方 dll 没有这两个成员（编译期 CS0117，运行期 MissingFieldException）。
    /// 这里改用官方公开 API 达成同样效果：
    ///   1. Addressables.ResourceManager.ResourceProviders.Add(自定义 IResourceProvider)
    ///   2. Addressables.AddResourceLocator(自定义 IResourceLocator)
    /// 于是 Addressables.LoadAssetAsync&lt;Sprite&gt;("Portraits/xxx.png") 这类调用会命中我们的 provider。
    /// </summary>
    public static class ModAddressables
    {
        private static string[] modPaths = new string[0];
        private static Func<string, Sprite> load = null;

        /// <summary>
        /// 记录 mod 目录与图片加载函数（HookMods 里调用一次）
        /// </summary>
        public static void Configure(string[] paths, Func<string, Sprite> loader)
        {
            modPaths = paths ?? new string[0];
            load = loader;
        }

        /// <summary>
        /// 幂等注册：provider / locator 缺失时才补上。
        /// 场景切换时可以再调一次，防止 Addressables 重新初始化后我们的注册被清掉。
        /// </summary>
        public static void Install()
        {
            if (load == null)
                return;

            try
            {
                var resourceManager = Addressables.ResourceManager;
                if (resourceManager == null)
                {
                    Debug.LogWarning("ModSupport: Addressables.ResourceManager 为空，换图功能未启用");
                    return;
                }

                bool hasProvider = false;
                foreach (var existing in resourceManager.ResourceProviders)
                {
                    if (existing != null && existing.ProviderId == ModSpriteProvider.ProviderIdName)
                    {
                        hasProvider = true;
                        break;
                    }
                }
                if (!hasProvider)
                    resourceManager.ResourceProviders.Add(new ModSpriteProvider(load));

                bool hasLocator = false;
                foreach (var existing in Addressables.ResourceLocators)
                {
                    if (existing != null && existing.LocatorId == ModSpriteLocator.LocatorIdName)
                    {
                        hasLocator = true;
                        break;
                    }
                }
                if (!hasLocator)
                {
                    Addressables.AddResourceLocator(new ModSpriteLocator(modPaths));
                    Debug.Log($"ModSupport: 已注册 Addressables 换图支持，mod 目录 {modPaths.Length} 个");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"ModSupport: 注册 Addressables 换图支持失败: {e}");
            }
        }
    }
}
