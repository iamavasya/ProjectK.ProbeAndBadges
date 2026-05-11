using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges.UnitTests;

public class BadgesCatalogTests
{
    [Fact]
    public void GetAll_ReturnsSeedBadges_WhenNoPathProvided()
    {
        var sut = new BadgesCatalog(new BadgesCatalogOptions
        {
            DataSource = BadgesDataSource.EmbeddedSeed
        });

        var all = sut.GetAll();

        Assert.NotEmpty(all);
        Assert.Equal(2, all.Count);
        Assert.Equal("seed-1.0.0", sut.Metadata.ParserVersion);
    }

    [Fact]
    public void GetById_ReturnsBadge_WhenIdExists()
    {
        var sut = new BadgesCatalog(new BadgesCatalogOptions
        {
            DataSource = BadgesDataSource.EmbeddedSeed
        });

        var badge = sut.GetById("avtoznavstvo-i");

        Assert.NotNull(badge);
        Assert.Equal("Автознавство I", badge!.Title);
    }

    [Fact]
    public void Query_FiltersByCountryAndText()
    {
        var sut = new BadgesCatalog(new BadgesCatalogOptions
        {
            DataSource = BadgesDataSource.EmbeddedSeed
        });

        var result = sut.Query(new BadgeQuery(Text: "актор", Country: "Україна"));

        Assert.Single(result);
        Assert.Equal("aktorstvo-i", result[0].Id);
    }

    [Fact]
    public void Query_ReturnsCachedResult_WhenCacheEnabled()
    {
        var sut = new BadgesCatalog(new BadgesCatalogOptions
        {
            DataSource = BadgesDataSource.EmbeddedSeed,
            EnableQueryCache = true
        });

        var query = new BadgeQuery(Country: "Україна");
        var first = sut.Query(query);
        var second = sut.Query(query);

        Assert.Same(first, second);
    }

    [Fact]
    public void Constructor_UsesExternalJson_WhenJsonFileSourceProvided()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, """
            {
              "Badges": [
                {
                  "Id": "custom-id",
                  "Title": "Custom Badge",
                  "ImagePath": "",
                  "Country": "Польща",
                  "Specialization": "Тест",
                  "Status": "Апробаційна",
                  "Level": 2,
                  "LastUpdated": "пн, 01.01.2024",
                  "SeekerRequirements": "A",
                  "InstructorRequirements": "B",
                  "FixNotes": []
                }
              ]
            }
            """);

            var sut = new BadgesCatalog(new BadgesCatalogOptions
            {
                DataSource = BadgesDataSource.JsonFile,
                JsonFilePath = tempPath
            });

            var badge = sut.GetById("custom-id");

            Assert.NotNull(badge);
            Assert.Equal("Custom Badge", badge!.Title);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Constructor_UsesReleaseZipSource_WhenZipProvided()
    {
        var tempDir = Directory.CreateTempSubdirectory("badges-catalog-tests");
        var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("badges.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""
                {
                  "Meta": {
                    "ParserVersion": "9.9.9",
                    "ToolAuthor": "Test",
                    "ParserComment": "Zip import",
                    "ParsedAtUtc": "2026-01-01T00:00:00Z",
                    "SourceUrl": "https://example.com",
                    "FixerEnabled": false,
                    "FixerMode": "off",
                    "TotalBadges": 1
                  },
                  "Badges": [
                    {
                      "Id": "zip-badge",
                      "Title": "Zip Badge",
                      "ImagePath": "",
                      "Country": "Україна",
                      "Specialization": "Тест",
                      "Status": "Затверджена",
                      "Level": 1,
                      "LastUpdated": "пн, 01.01.2024",
                      "SeekerRequirements": "A",
                      "InstructorRequirements": "B",
                      "FixNotes": []
                    }
                  ]
                }
                """);
            }

            var sut = new BadgesCatalog(new BadgesCatalogOptions
            {
                DataSource = BadgesDataSource.ReleaseDataZip,
                ReleaseDataZipPath = zipPath,
                ReleaseDataZipEntryPath = "badges.json",
                EnableAssetsExtraction = false
            });

            var badge = sut.GetById("zip-badge");

            Assert.NotNull(badge);
            Assert.Equal("Zip Badge", badge!.Title);
            Assert.Equal("9.9.9", sut.Metadata.ParserVersion);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Constructor_UsesReleaseZipUrlSource_WhenUrlProvided()
    {
        var zipBytes = BuildZipBytes("""
        {
          "Meta": {
            "ParserVersion": "2.1.0",
            "ToolAuthor": "Test",
            "ParserComment": "Remote zip import",
            "ParsedAtUtc": "2026-01-01T00:00:00Z",
            "SourceUrl": "https://example.com",
            "FixerEnabled": false,
            "FixerMode": "off",
            "TotalBadges": 1
          },
          "Badges": [
            {
              "Id": "remote-zip-badge",
              "Title": "Remote Zip Badge",
              "ImagePath": "",
              "Country": "Україна",
              "Specialization": "Тест",
              "Status": "Затверджена",
              "Level": 1,
              "LastUpdated": "пн, 01.01.2024",
              "SeekerRequirements": "A",
              "InstructorRequirements": "B",
              "FixNotes": []
            }
          ]
        }
        """);

        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serveTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/zip";
            context.Response.ContentLength64 = zipBytes.Length;
            await context.Response.OutputStream.WriteAsync(zipBytes);
            context.Response.Close();
        });

        try
        {
            var sut = new BadgesCatalog(new BadgesCatalogOptions
            {
                DataSource = BadgesDataSource.ReleaseDataZipUrl,
                ReleaseDataZipUrl = $"{prefix}plast-badges-data.zip",
                ReleaseDataZipEntryPath = "badges.json",
                RemoteRequestTimeoutSeconds = 10,
                EnableAssetsExtraction = false
            });

            var badge = sut.GetById("remote-zip-badge");

            Assert.NotNull(badge);
            Assert.Equal("Remote Zip Badge", badge!.Title);
            Assert.Equal("2.1.0", sut.Metadata.ParserVersion);
        }
        finally
        {
            listener.Stop();
            await serveTask;
        }
    }

        [Fact]
        public async Task Constructor_UsesLatestReleaseApi_WhenAutoModeEnabled()
        {
                var zipBytes = BuildZipBytes("""
                {
                    "Meta": {
                        "ParserVersion": "3.0.0",
                        "ToolAuthor": "Test",
                        "ParserComment": "Latest release API import",
                        "ParsedAtUtc": "2026-01-01T00:00:00Z",
                        "SourceUrl": "https://example.com",
                        "FixerEnabled": false,
                        "FixerMode": "off",
                        "TotalBadges": 1
                    },
                    "Badges": [
                        {
                            "Id": "latest-badge",
                            "Title": "Latest Badge",
                            "ImagePath": "",
                            "Country": "Україна",
                            "Specialization": "Тест",
                            "Status": "Затверджена",
                            "Level": 1,
                            "LastUpdated": "пн, 01.01.2024",
                            "SeekerRequirements": "A",
                            "InstructorRequirements": "B",
                            "FixNotes": []
                        }
                    ]
                }
                """);

                var port = GetFreeTcpPort();
                var prefix = $"http://127.0.0.1:{port}/";
                var apiPath = "api/latest";
                var downloadPath = "download/plast-badges-data-3.0.0.zip";

                using var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();

                var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)).ToLowerInvariant();

                var serveTask = Task.Run(async () =>
                {
                        for (var i = 0; i < 3; i++)
                        {
                                var context = await listener.GetContextAsync();
                                var path = context.Request.Url?.AbsolutePath?.TrimStart('/') ?? string.Empty;

                                if (string.Equals(path, apiPath, StringComparison.OrdinalIgnoreCase))
                                {
                                        var payload = $$"""
                                        {
                                            "assets": [
                                                {
                                                    "name": "plast-badges-data-3.0.0.zip",
                                                    "browser_download_url": "{{prefix}}{{downloadPath}}"
                                                },
                                                {
                                                    "name": "plast-badges-data-3.0.0.zip.sha256",
                                                    "browser_download_url": "{{prefix}}download/plast-badges-data-3.0.0.zip.sha256"
                                                }
                                            ]
                                        }
                                        """;

                                        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                                        context.Response.StatusCode = 200;
                                        context.Response.ContentType = "application/json";
                                        context.Response.ContentLength64 = bytes.Length;
                                        await context.Response.OutputStream.WriteAsync(bytes);
                                        context.Response.Close();
                                        continue;
                                }

                                    if (string.Equals(path, "download/plast-badges-data-3.0.0.zip.sha256", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var bytes = System.Text.Encoding.UTF8.GetBytes(checksum);
                                        context.Response.StatusCode = 200;
                                        context.Response.ContentType = "text/plain";
                                        context.Response.ContentLength64 = bytes.Length;
                                        await context.Response.OutputStream.WriteAsync(bytes);
                                        context.Response.Close();
                                        continue;
                                    }

                                if (string.Equals(path, downloadPath, StringComparison.OrdinalIgnoreCase))
                                {
                                        context.Response.StatusCode = 200;
                                        context.Response.ContentType = "application/zip";
                                        context.Response.ContentLength64 = zipBytes.Length;
                                        await context.Response.OutputStream.WriteAsync(zipBytes);
                                        context.Response.Close();
                                        continue;
                                }

                                context.Response.StatusCode = 404;
                                context.Response.Close();
                        }
                });

                try
                {
                        var sut = new BadgesCatalog(new BadgesCatalogOptions
                        {
                                DataSource = BadgesDataSource.Auto,
                                LatestReleaseApiUrl = $"{prefix}{apiPath}",
                                LatestReleaseAssetNamePattern = "plast-badges-data-*.zip",
                                ReleaseDataZipEntryPath = "badges.json",
                                AutoFallbackToEmbeddedSeed = false,
                            VerifyReleaseZipChecksum = true,
                            RequireReleaseZipChecksumAsset = true,
                            RemoteRequestTimeoutSeconds = 10,
                            EnableAssetsExtraction = false
                        });

                        var badge = sut.GetById("latest-badge");

                        Assert.NotNull(badge);
                        Assert.Equal("Latest Badge", badge!.Title);
                        Assert.Equal("3.0.0", sut.Metadata.ParserVersion);
                }
                finally
                {
                        listener.Stop();
                        await serveTask;
                }
        }

        [Fact]
        public async Task Constructor_UsesCachedZip_WhenLatestReleaseReturns304AndEtagMatches()
        {
                var zipBytes = BuildZipBytes("""
                {
                    "Meta": {
                        "ParserVersion": "4.0.0",
                        "ToolAuthor": "Test",
                        "ParserComment": "ETag cache test",
                        "ParsedAtUtc": "2026-01-01T00:00:00Z",
                        "SourceUrl": "https://example.com",
                        "FixerEnabled": false,
                        "FixerMode": "off",
                        "TotalBadges": 1
                    },
                    "Badges": [
                        {
                            "Id": "etag-badge",
                            "Title": "ETag Badge",
                            "ImagePath": "",
                            "Country": "Україна",
                            "Specialization": "Тест",
                            "Status": "Затверджена",
                            "Level": 1,
                            "LastUpdated": "пн, 01.01.2024",
                            "SeekerRequirements": "A",
                            "InstructorRequirements": "B",
                            "FixNotes": []
                        }
                    ]
                }
                """);

                var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)).ToLowerInvariant();

                var tempCacheDir = Directory.CreateTempSubdirectory("badges-catalog-cache-tests");
                var port = GetFreeTcpPort();
                var prefix = $"http://127.0.0.1:{port}/";
                var apiPath = "api/latest";
                var downloadPath = "download/plast-badges-data-4.0.0.zip";
                var checksumPath = "download/plast-badges-data-4.0.0.zip.sha256";
                var etag = "\"v4-cache\"";

                var latestApiCalls = 0;
                var zipCalls = 0;

                var serverConfig = new LatestReleaseServerConfig(
                    RequestCount: 4,
                    ApiPath: apiPath,
                    DownloadPath: downloadPath,
                    ChecksumPath: checksumPath,
                    Prefix: prefix,
                    Etag: etag,
                    ZipBytes: zipBytes,
                    Checksum: checksum);

                using var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();

                var serveTask = Task.Run(() => ServeLatestReleaseWithEtagAsync(
                    listener,
                    serverConfig,
                    onLatestApiCall: () => latestApiCalls++,
                    onZipCall: () => zipCalls++));

                try
                {
                        var commonOptions = new BadgesCatalogOptions
                        {
                                DataSource = BadgesDataSource.Auto,
                                LatestReleaseApiUrl = $"{prefix}{apiPath}",
                                LatestReleaseAssetNamePattern = "plast-badges-data-*.zip",
                                LocalCacheDirectoryPath = tempCacheDir.FullName,
                                AutoFallbackToEmbeddedSeed = false,
                                EnableLatestReleaseEtagCache = true,
                                EnableLocalReleaseZipCache = true,
                                VerifyReleaseZipChecksum = true,
                                RequireReleaseZipChecksumAsset = true,
                                RemoteRequestTimeoutSeconds = 10,
                                EnableAssetsExtraction = false
                        };

                        var first = new BadgesCatalog(commonOptions);
                        var second = new BadgesCatalog(commonOptions);

                        Assert.NotNull(first.GetById("etag-badge"));
                        Assert.NotNull(second.GetById("etag-badge"));
                        Assert.Equal(2, latestApiCalls);
                        Assert.Equal(1, zipCalls);
                }
                finally
                {
                        listener.Stop();
                        await serveTask;
                        tempCacheDir.Delete(recursive: true);
                }
        }

        [Fact]
        public void Constructor_FallsBackToEmbeddedSeed_WhenAutoModeRemoteFails()
        {
            var tempCacheDir = Directory.CreateTempSubdirectory("badges-catalog-fallback-tests");
            try
            {
                var sut = new BadgesCatalog(new BadgesCatalogOptions
                {
                    DataSource = BadgesDataSource.Auto,
                    LatestReleaseApiUrl = "http://127.0.0.1:1/releases/latest",
                    LocalCacheDirectoryPath = tempCacheDir.FullName,
                    EnableLocalReleaseZipCache = false,
                    EnableLatestReleaseEtagCache = false,
                    AutoFallbackToEmbeddedSeed = true,
                    RemoteRequestTimeoutSeconds = 1
                });

                var all = sut.GetAll();

                Assert.Equal(2, all.Count);
                Assert.Equal("seed-1.0.0", sut.Metadata.ParserVersion);
            }
            finally
            {
                tempCacheDir.Delete(recursive: true);
            }
        }

        [Fact]
        public void Constructor_ExtractsAssetsSnapshot_WhenReleaseZipContainsImages()
        {
                var tempDir = Directory.CreateTempSubdirectory("badges-catalog-assets-tests");
                var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");
                var assetsRootDir = Directory.CreateTempSubdirectory("badges-catalog-assets-root");

                try
                {
                        var zipBytes = BuildZipBytes(
                                """
                                {
                                    "Meta": {
                                        "ParserVersion": "5.0.0",
                                        "ToolAuthor": "Test",
                                        "ParserComment": "Assets extraction",
                                        "ParsedAtUtc": "2026-01-01T00:00:00Z",
                                        "SourceUrl": "https://example.com",
                                        "FixerEnabled": false,
                                        "FixerMode": "off",
                                        "TotalBadges": 1
                                    },
                                    "Badges": [
                                        {
                                            "Id": "asset-badge",
                                            "Title": "Asset Badge",
                                            "ImagePath": "/badges_images/asset-badge.svg",
                                            "Country": "Україна",
                                            "Specialization": "Тест",
                                            "Status": "Затверджена",
                                            "Level": 1,
                                            "LastUpdated": "пн, 01.01.2024",
                                            "SeekerRequirements": "A",
                                            "InstructorRequirements": "B",
                                            "FixNotes": []
                                        }
                                    ]
                                }
                                """,
                                ("badges_images/asset-badge.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"));

                        File.WriteAllBytes(zipPath, zipBytes);

                        var sut = new BadgesCatalog(new BadgesCatalogOptions
                        {
                                DataSource = BadgesDataSource.ReleaseDataZip,
                                ReleaseDataZipPath = zipPath,
                                ReleaseDataZipEntryPath = "badges.json",
                                EnableAssetsExtraction = true,
                                AssetsRootDirectory = assetsRootDir.FullName,
                                AssetsImagesDirectoryName = "badges_images",
                                ValidateExtractedImagesAgainstCatalog = true
                        });

                        Assert.False(string.IsNullOrWhiteSpace(sut.AssetsDirectoryPath));
                        Assert.True(Directory.Exists(sut.AssetsDirectoryPath!));
                        Assert.True(File.Exists(Path.Combine(sut.AssetsDirectoryPath!, "asset-badge.svg")));
                        Assert.NotNull(sut.AssetsSnapshot);
                        Assert.Equal(sut.AssetsDirectoryPath, sut.AssetsSnapshot!.ImagesDirectoryPath);
                        Assert.Equal(1, sut.AssetsSnapshot.ImagesCount);
                        Assert.False(string.IsNullOrWhiteSpace(sut.AssetsSnapshot.SourceZipSha256));
                }
                finally
                {
                        if (File.Exists(zipPath))
                        {
                                File.Delete(zipPath);
                        }

                        tempDir.Delete(recursive: true);
                        assetsRootDir.Delete(recursive: true);
                }
        }

        [Fact]
        public void Constructor_ReusesExistingAssetsSnapshot_WhenZipShaUnchanged()
        {
                var tempDir = Directory.CreateTempSubdirectory("badges-catalog-assets-reuse-tests");
                var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");
                var assetsRootDir = Directory.CreateTempSubdirectory("badges-catalog-assets-reuse-root");

                try
                {
                        var zipBytes = BuildZipBytes(
                                """
                                {
                                    "Meta": {
                                        "ParserVersion": "5.0.1",
                                        "ToolAuthor": "Test",
                                        "ParserComment": "Assets extraction reuse",
                                        "ParsedAtUtc": "2026-01-01T00:00:00Z",
                                        "SourceUrl": "https://example.com",
                                        "FixerEnabled": false,
                                        "FixerMode": "off",
                                        "TotalBadges": 1
                                    },
                                    "Badges": [
                                        {
                                            "Id": "asset-badge-reuse",
                                            "Title": "Asset Badge Reuse",
                                            "ImagePath": "/badges_images/asset-badge-reuse.svg",
                                            "Country": "Україна",
                                            "Specialization": "Тест",
                                            "Status": "Затверджена",
                                            "Level": 1,
                                            "LastUpdated": "пн, 01.01.2024",
                                            "SeekerRequirements": "A",
                                            "InstructorRequirements": "B",
                                            "FixNotes": []
                                        }
                                    ]
                                }
                                """,
                                ("badges_images/asset-badge-reuse.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"));

                        File.WriteAllBytes(zipPath, zipBytes);

                        var options = new BadgesCatalogOptions
                        {
                                DataSource = BadgesDataSource.ReleaseDataZip,
                                ReleaseDataZipPath = zipPath,
                                ReleaseDataZipEntryPath = "badges.json",
                                EnableAssetsExtraction = true,
                                AssetsRootDirectory = assetsRootDir.FullName,
                                AssetsImagesDirectoryName = "badges_images",
                                ValidateExtractedImagesAgainstCatalog = true
                        };

                        var first = new BadgesCatalog(options);
                        var second = new BadgesCatalog(options);

                        Assert.False(string.IsNullOrWhiteSpace(first.AssetsDirectoryPath));
                        Assert.NotNull(first.AssetsSnapshot);
                        Assert.NotNull(second.AssetsSnapshot);
                        Assert.Equal(first.AssetsDirectoryPath, second.AssetsDirectoryPath);
                        Assert.Equal(first.AssetsSnapshot!.SourceZipSha256, second.AssetsSnapshot!.SourceZipSha256);
                }
                finally
                {
                        if (File.Exists(zipPath))
                        {
                                File.Delete(zipPath);
                        }

                        tempDir.Delete(recursive: true);
                        assetsRootDir.Delete(recursive: true);
                }
        }

                [Fact]
                public void Constructor_ContinuesAssetsExtraction_WhenMissingImagesPolicyIsWarn()
                {
                    var tempDir = Directory.CreateTempSubdirectory("badges-catalog-assets-warn-tests");
                    var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");
                    var assetsRootDir = Directory.CreateTempSubdirectory("badges-catalog-assets-warn-root");

                    try
                    {
                        var zipBytes = BuildZipBytes(
                            """
                            {
                                "Meta": {
                                "ParserVersion": "5.1.0",
                                "ToolAuthor": "Test",
                                "ParserComment": "Assets warn policy",
                                "ParsedAtUtc": "2026-01-01T00:00:00Z",
                                "SourceUrl": "https://example.com",
                                "FixerEnabled": false,
                                "FixerMode": "off",
                                "TotalBadges": 1
                                },
                                "Badges": [
                                {
                                    "Id": "asset-badge-warn",
                                    "Title": "Asset Badge Warn",
                                    "ImagePath": "/badges_images/missing.svg",
                                    "Country": "Україна",
                                    "Specialization": "Тест",
                                    "Status": "Затверджена",
                                    "Level": 1,
                                    "LastUpdated": "пн, 01.01.2024",
                                    "SeekerRequirements": "A",
                                    "InstructorRequirements": "B",
                                    "FixNotes": []
                                }
                                ]
                            }
                            """,
                            ("badges_images/present.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"));

                        File.WriteAllBytes(zipPath, zipBytes);

                        var sut = new BadgesCatalog(new BadgesCatalogOptions
                        {
                            DataSource = BadgesDataSource.ReleaseDataZip,
                            ReleaseDataZipPath = zipPath,
                            ReleaseDataZipEntryPath = "badges.json",
                            EnableAssetsExtraction = true,
                            AssetsRootDirectory = assetsRootDir.FullName,
                            AssetsImagesDirectoryName = "badges_images",
                            ValidateExtractedImagesAgainstCatalog = true,
                            MissingImagesPolicy = AssetsMissingImagesPolicy.Warn
                        });

                        Assert.True(sut.HasAssets);
                        Assert.NotNull(sut.AssetsSnapshot);
                        Assert.Equal(1, sut.AssetsSnapshot!.ImagesCount);
                    }
                    finally
                    {
                        if (File.Exists(zipPath))
                        {
                            File.Delete(zipPath);
                        }

                        tempDir.Delete(recursive: true);
                        assetsRootDir.Delete(recursive: true);
                    }
                }

                [Fact]
                public void Constructor_FailsAssetsExtraction_WhenMaxExtractedImageEntriesExceeded()
                {
                    var tempDir = Directory.CreateTempSubdirectory("badges-catalog-assets-limit-entries-tests");
                    var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");
                    var assetsRootDir = Directory.CreateTempSubdirectory("badges-catalog-assets-limit-entries-root");

                    try
                    {
                        var zipBytes = BuildZipBytes(
                            BuildAssetsLimitJson("entries-limit"),
                            ("badges_images/a.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"),
                            ("badges_images/b.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"));

                        File.WriteAllBytes(zipPath, zipBytes);

                        var ex = Assert.Throws<InvalidDataException>(() => new BadgesCatalog(new BadgesCatalogOptions
                        {
                            DataSource = BadgesDataSource.ReleaseDataZip,
                            ReleaseDataZipPath = zipPath,
                            ReleaseDataZipEntryPath = "badges.json",
                            EnableAssetsExtraction = true,
                            AssetsRootDirectory = assetsRootDir.FullName,
                            ValidateExtractedImagesAgainstCatalog = false,
                            MaxExtractedImageEntries = 1
                        }));

                        Assert.Contains("MaxExtractedImageEntries", ex.Message);
                    }
                    finally
                    {
                        if (File.Exists(zipPath))
                        {
                            File.Delete(zipPath);
                        }

                        tempDir.Delete(recursive: true);
                        assetsRootDir.Delete(recursive: true);
                    }
                }

                [Fact]
                public void Constructor_FailsAssetsExtraction_WhenSingleImageSizeLimitExceeded()
                {
                    var tempDir = Directory.CreateTempSubdirectory("badges-catalog-assets-limit-single-tests");
                    var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");
                    var assetsRootDir = Directory.CreateTempSubdirectory("badges-catalog-assets-limit-single-root");

                    try
                    {
                        var largeContent = new string('x', 2048);
                        var zipBytes = BuildZipBytes(
                            BuildAssetsLimitJson("single-size-limit"),
                            ("badges_images/a.svg", largeContent));

                        File.WriteAllBytes(zipPath, zipBytes);

                        var ex = Assert.Throws<InvalidDataException>(() => new BadgesCatalog(new BadgesCatalogOptions
                        {
                            DataSource = BadgesDataSource.ReleaseDataZip,
                            ReleaseDataZipPath = zipPath,
                            ReleaseDataZipEntryPath = "badges.json",
                            EnableAssetsExtraction = true,
                            AssetsRootDirectory = assetsRootDir.FullName,
                            ValidateExtractedImagesAgainstCatalog = false,
                            MaxExtractedSingleImageBytes = 256
                        }));

                        Assert.Contains("MaxExtractedSingleImageBytes", ex.Message);
                    }
                    finally
                    {
                        if (File.Exists(zipPath))
                        {
                            File.Delete(zipPath);
                        }

                        tempDir.Delete(recursive: true);
                        assetsRootDir.Delete(recursive: true);
                    }
                }

                [Fact]
                public void Constructor_FailsAssetsExtraction_WhenTotalImagesSizeLimitExceeded()
                {
                    var tempDir = Directory.CreateTempSubdirectory("badges-catalog-assets-limit-total-tests");
                    var zipPath = Path.Combine(tempDir.FullName, "release-data.zip");
                    var assetsRootDir = Directory.CreateTempSubdirectory("badges-catalog-assets-limit-total-root");

                    try
                    {
                        var contentA = new string('a', 120);
                        var contentB = new string('b', 120);
                        var zipBytes = BuildZipBytes(
                            BuildAssetsLimitJson("total-size-limit"),
                            ("badges_images/a.svg", contentA),
                            ("badges_images/b.svg", contentB));

                        File.WriteAllBytes(zipPath, zipBytes);

                        var ex = Assert.Throws<InvalidDataException>(() => new BadgesCatalog(new BadgesCatalogOptions
                        {
                            DataSource = BadgesDataSource.ReleaseDataZip,
                            ReleaseDataZipPath = zipPath,
                            ReleaseDataZipEntryPath = "badges.json",
                            EnableAssetsExtraction = true,
                            AssetsRootDirectory = assetsRootDir.FullName,
                            ValidateExtractedImagesAgainstCatalog = false,
                            MaxExtractedSingleImageBytes = 10_000,
                            MaxExtractedImagesTotalBytes = 200
                        }));

                        Assert.Contains("MaxExtractedImagesTotalBytes", ex.Message);
                    }
                    finally
                    {
                        if (File.Exists(zipPath))
                        {
                            File.Delete(zipPath);
                        }

                        tempDir.Delete(recursive: true);
                        assetsRootDir.Delete(recursive: true);
                    }
                }

                [Fact]
                public async Task Constructor_CleansDownloadedZipCache_WhenRetentionLimitReached()
                {
                    var tempCacheDir = Directory.CreateTempSubdirectory("badges-catalog-downloaded-cache-retention");
                    var zipBytesV1 = BuildZipBytes(BuildRemoteUrlJson("remote-retention-v1", "6.0.1"));
                    var zipBytesV2 = BuildZipBytes(BuildRemoteUrlJson("remote-retention-v2", "6.0.2"));

                    async Task LoadOnceAsync(byte[] zipBytes, string route)
                    {
                        var port = GetFreeTcpPort();
                        var prefix = $"http://127.0.0.1:{port}/";
                        using var listener = new HttpListener();
                        listener.Prefixes.Add(prefix);
                        listener.Start();

                        var serveTask = Task.Run(async () =>
                        {
                            var context = await listener.GetContextAsync();
                            var path = context.Request.Url?.AbsolutePath?.TrimStart('/') ?? string.Empty;
                            if (!string.Equals(path, route, StringComparison.OrdinalIgnoreCase))
                            {
                                context.Response.StatusCode = 404;
                                context.Response.Close();
                                return;
                            }

                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/zip";
                            context.Response.ContentLength64 = zipBytes.Length;
                            await context.Response.OutputStream.WriteAsync(zipBytes);
                            context.Response.Close();
                        });

                        try
                        {
                            var sut = new BadgesCatalog(new BadgesCatalogOptions
                            {
                                DataSource = BadgesDataSource.ReleaseDataZipUrl,
                                ReleaseDataZipUrl = $"{prefix}{route}",
                                ReleaseDataZipEntryPath = "badges.json",
                                LocalCacheDirectoryPath = tempCacheDir.FullName,
                                EnableLocalReleaseZipCache = true,
                                EnableAssetsExtraction = false,
                                DownloadedZipRetentionCount = 1,
                                RemoteRequestTimeoutSeconds = 10
                            });

                            Assert.NotNull(sut.GetAll());
                        }
                        finally
                        {
                            listener.Stop();
                            await serveTask;
                        }
                    }

                    try
                    {
                        await LoadOnceAsync(zipBytesV1, "download/v1.zip");
                        await LoadOnceAsync(zipBytesV2, "download/v2.zip");

                        var downloadedFiles = Directory
                            .GetFiles(tempCacheDir.FullName, "downloaded-*.zip", SearchOption.TopDirectoryOnly)
                            .Select(Path.GetFileName)
                            .ToArray();

                        Assert.Single(downloadedFiles);

                        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytesV2)).ToLowerInvariant();
                        Assert.Equal($"downloaded-{expectedSha}.zip", downloadedFiles[0]);
                    }
                    finally
                    {
                        tempCacheDir.Delete(recursive: true);
                    }
                }

                private static async Task ServeLatestReleaseWithEtagAsync(
                    HttpListener listener,
                    LatestReleaseServerConfig config,
                    Action onLatestApiCall,
                    Action onZipCall)
                {
                    try
                    {
                        for (var i = 0; i < config.RequestCount; i++)
                        {
                            var context = await listener.GetContextAsync();
                            var path = context.Request.Url?.AbsolutePath?.TrimStart('/') ?? string.Empty;

                            if (await TryHandleLatestApiRequestAsync(context, path, config, onLatestApiCall))
                            {
                                continue;
                            }

                            if (await TryHandleZipRequestAsync(context, path, config, onZipCall))
                            {
                                continue;
                            }

                            if (await TryHandleChecksumRequestAsync(context, path, config))
                            {
                                continue;
                            }

                            context.Response.StatusCode = 404;
                            context.Response.Close();
                        }
                    }
                    catch (HttpListenerException)
                    {
                        // Listener may be stopped by test teardown while awaiting context.
                    }
                    catch (ObjectDisposedException)
                    {
                        // Listener may already be disposed during teardown.
                    }
                }

                private static async Task<bool> TryHandleLatestApiRequestAsync(
                    HttpListenerContext context,
                    string path,
                    LatestReleaseServerConfig config,
                    Action onLatestApiCall)
                {
                    if (!string.Equals(path, config.ApiPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    onLatestApiCall();
                    var incomingEtag = context.Request.Headers["If-None-Match"];
                    if (string.Equals(incomingEtag, config.Etag, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 304;
                        context.Response.Headers["ETag"] = config.Etag;
                        context.Response.Close();
                        return true;
                    }

                    var payload = $$"""
                    {
                        "assets": [
                            {
                                "name": "plast-badges-data-4.0.0.zip",
                                "browser_download_url": "{{config.Prefix}}{{config.DownloadPath}}"
                            },
                            {
                                "name": "plast-badges-data-4.0.0.zip.sha256",
                                "browser_download_url": "{{config.Prefix}}{{config.ChecksumPath}}"
                            }
                        ]
                    }
                    """;

                    var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.Headers["ETag"] = config.Etag;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();

                    return true;
                }

                private static async Task<bool> TryHandleZipRequestAsync(
                    HttpListenerContext context,
                    string path,
                    LatestReleaseServerConfig config,
                    Action onZipCall)
                {
                    if (!string.Equals(path, config.DownloadPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    onZipCall();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/zip";
                    context.Response.ContentLength64 = config.ZipBytes.Length;
                    await context.Response.OutputStream.WriteAsync(config.ZipBytes);
                    context.Response.Close();

                    return true;
                }

                private static async Task<bool> TryHandleChecksumRequestAsync(
                    HttpListenerContext context,
                    string path,
                    LatestReleaseServerConfig config)
                {
                    if (!string.Equals(path, config.ChecksumPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var bytes = System.Text.Encoding.UTF8.GetBytes(config.Checksum);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();

                    return true;
                }

                private sealed record LatestReleaseServerConfig(
                    int RequestCount,
                    string ApiPath,
                    string DownloadPath,
                    string ChecksumPath,
                    string Prefix,
                    string Etag,
                    byte[] ZipBytes,
                    string Checksum);

                private static string BuildAssetsLimitJson(string id)
                {
                    return $$"""
                    {
                      "Meta": {
                        "ParserVersion": "6.0.0",
                        "ToolAuthor": "Test",
                        "ParserComment": "Assets limits",
                        "ParsedAtUtc": "2026-01-01T00:00:00Z",
                        "SourceUrl": "https://example.com",
                        "FixerEnabled": false,
                        "FixerMode": "off",
                        "TotalBadges": 1
                      },
                      "Badges": [
                        {
                          "Id": "{{id}}",
                          "Title": "Assets Limits",
                          "ImagePath": "",
                          "Country": "Україна",
                          "Specialization": "Тест",
                          "Status": "Затверджена",
                          "Level": 1,
                          "LastUpdated": "пн, 01.01.2024",
                          "SeekerRequirements": "A",
                          "InstructorRequirements": "B",
                          "FixNotes": []
                        }
                      ]
                    }
                    """;
                }

                private static string BuildRemoteUrlJson(string id, string parserVersion)
                {
                    return $$"""
                    {
                      "Meta": {
                        "ParserVersion": "{{parserVersion}}",
                        "ToolAuthor": "Test",
                        "ParserComment": "Remote retention",
                        "ParsedAtUtc": "2026-01-01T00:00:00Z",
                        "SourceUrl": "https://example.com",
                        "FixerEnabled": false,
                        "FixerMode": "off",
                        "TotalBadges": 1
                      },
                      "Badges": [
                        {
                          "Id": "{{id}}",
                          "Title": "Remote Retention",
                          "ImagePath": "",
                          "Country": "Україна",
                          "Specialization": "Тест",
                          "Status": "Затверджена",
                          "Level": 1,
                          "LastUpdated": "пн, 01.01.2024",
                          "SeekerRequirements": "A",
                          "InstructorRequirements": "B",
                          "FixNotes": []
                        }
                      ]
                    }
                    """;
                }

        private static byte[] BuildZipBytes(string badgesJson, params (string EntryPath, string Content)[] extraEntries)
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("badges.json");
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(badgesJson);
            }

            foreach (var (entryPath, content) in extraEntries)
            {
                var extraEntry = archive.CreateEntry(entryPath);
                using var extraWriter = new StreamWriter(extraEntry.Open());
                extraWriter.Write(content);
            }
        }

        return zipStream.ToArray();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
