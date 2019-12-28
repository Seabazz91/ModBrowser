﻿using Ionic.Zlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModBrowser.Data;
using ModBrowser.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ModBrowser.Services
{
    public class SyncService : BackgroundService
    {
        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<SyncService> _logger;
        internal static HttpClient Http = new HttpClient() { Timeout = new TimeSpan(0, 5, 0) };
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
        internal static Version tModLoaderVersion = new Version("0.11.6.1");

        public SyncService(IServiceScopeFactory scopeFactory, ILogger<SyncService> logger)
        {
            this.scopeFactory = scopeFactory;
            this._logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Directory.Exists("mods"))
            {
                Directory.CreateDirectory("mods");
            }

            await Task.Delay(TimeSpan.FromSeconds(15));

            while (true)
            {
                string str;
                try
                {
                    str = await Http.PostAsync("http://javid.ddns.net/tModLoader/listmods.php", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["modloaderversion"] = tModLoaderVersion.ToString(),
                        ["platform"] = "w",
                        ["netversion"] = "4.0",
                    })).Result.Content.ReadAsStringAsync();
                    this._logger.LogInformation("Start Sync");
                }
                catch
                {
                    this._logger.LogInformation("POST listmods.php fail, retry");
                    await Task.Delay(Interval);
                    continue;
                }

                JObject json;
                try
                {
                    json = (JObject)JsonConvert.DeserializeObject(str);
                }
                catch
                {
                    this._logger.LogInformation("Json Deserialize exception, retry");
                    await Task.Delay(Interval);
                    continue;
                }

                if (json.ContainsKey("update"))
                {
                    tModLoaderVersion = new Version(json["update"].ToString());
                    this._logger.LogInformation($"Update to {json["update"]}");
                    continue;
                }

                if (!json.ContainsKey("modlist_compressed"))
                {
                    this._logger.LogInformation("modlist_compressed not found, retry");
                    await Task.Delay(Interval);
                    continue;
                }

                string list;
                using (var ms = new MemoryStream(Convert.FromBase64String(json["modlist_compressed"].ToString())))
                {
                    using (var stream = new GZipStream(ms, CompressionMode.Decompress))
                    {
                        using (var sr = new StreamReader(stream))
                        {
                            list = sr.ReadToEnd();
                        }
                    }
                }

                var modlist = JsonConvert.DeserializeObject<List<Mod>>(list);
                this._logger.LogInformation($"Unpacked Mod list ({modlist.Count})");

                var descriptions = Http.GetStringAsync("http://javid.ddns.net/tModLoader/tools/querymodnamehomepagedescription.php").Result;
                var desclist = JsonConvert.DeserializeObject<List<Mod>>(descriptions).ToDictionary(i => i.Name);
                foreach (var item in modlist)
                {
                    item.Homepage = !string.IsNullOrWhiteSpace(desclist[item.Name].Homepage) ? desclist[item.Name].Homepage : null;
                    item.Description = !string.IsNullOrWhiteSpace(desclist[item.Name].Description) ? desclist[item.Name].Description : null;
                }
#if PAR
                Parallel.ForEach(modlist, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 2
                }, async item =>
#else
                foreach (var item in modlist)
#endif
                {
                    try
                    {
                        using (var scope = this.scopeFactory.CreateScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var found = db.Mod.Find(item.Name);
                            item.ModLoaderVersion ??= found?.ModLoaderVersion;

                            var mayNeedIcon = !File.Exists(item.IconPath());
                            if (found?.Version != item.Version)
                            {
                                mayNeedIcon = true;
                                this._logger.LogInformation($"Mod {item.DisplayName} ({item.Name}) {found?.Version} => {item.Version}");
                                var result = await Http.GetByteArrayAsync($"http://javid.ddns.net/tModLoader/download.php?Down=mods/{item.Name}.tmod");
                                File.WriteAllBytes(item.FilePath(), result);
                                File.SetLastWriteTimeUtc(item.FilePath(), item.GetUpdateTimestamp());
                                ExtractInfo(result, item);
                                item.Size = (int)new FileInfo(item.FilePath()).Length;
                            }

                            if (found?.Size == 0)
                            {
                                item.Size = (int)new FileInfo(item.FilePath()).Length;
                            }

                            if (mayNeedIcon && !string.IsNullOrWhiteSpace(item.IconURL))
                            {
                                var result = await Http.GetByteArrayAsync(item.IconURL);
                                File.WriteAllBytes(item.IconPath(), result);
                                File.SetLastWriteTimeUtc(item.IconPath(), item.GetUpdateTimestamp());
                            }

                            if (found == null)
                            {
                                this._logger.LogInformation($"Mod {item.DisplayName} ({item.Name}) created.");
                                db.Mod.Add(item);
                            }
                            else
                            {
                                db.Entry(found).CurrentValues.SetValues(item);
                                db.Mod.Update(found);
                            }

                            db.SaveChanges();
                        }
                    }
                    catch (Exception e)
                    {
                        this._logger.LogError($"Mod Update {item.DisplayName} ({item.Name}) : " + e.ToString());
                    }
                }
#if PAR
                );
#endif

                this._logger.LogInformation("End of Sync, Sleep");
                await Task.Delay(Interval);
            }
        }

        private static void ExtractInfo(byte[] file, Mod mod)
        {
            using (var ms = new MemoryStream(file))
            {
                using (var br = new BinaryReader(ms))
                {
                    br.ReadBytes(4);
                    mod.ModLoaderVersion = "tModLoader v" + br.ReadString();
                }
            }
        }
    }
}
