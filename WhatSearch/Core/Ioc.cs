using WhatSearch.Service;
using System;
using WhatSearch.Services.Interfaces;
using WhatSearch.Services;
using WhatSearch.Jobs;
using WhatSearch.DataProviders;
using WhatSearch.DataProviders.Interfaces;
using Autofac;
using Microsoft.Extensions.Caching.Memory;

namespace WhatSearch.Core
{
    public class Ioc
    {
        private static IContainer container;
        public static void Register()
        {
            var builder = new ContainerBuilder();
            
            builder.RegisterInstance(SystemConfig.Reload()).SingleInstance();
            builder.RegisterType<SimpleDocumentService>().As<IDocumentService>().SingleInstance();
            builder.RegisterType<SimpleSearchService>().As<ISearchSercice>().SingleInstance();
            builder.RegisterType<FileWatcherService>().As<IFileWatcherService>().SingleInstance();
            builder.RegisterType<ChineseConverter>().As<IChineseConverter>().SingleInstance();
            builder.RegisterType<FolderIdManager>().As<IFolderIdManager>().SingleInstance();
            
            builder.RegisterType<MainService>().As<IMainService>().SingleInstance();
            builder.RegisterType<MemberProvider>().As<IMemberProvider>().SingleInstance();
            builder.RegisterType<UserService>().As<IUserService>().SingleInstance();
            builder.RegisterType<FavoriteService>().As<IFavoriteService>().SingleInstance();
            builder.RegisterType<PinService>().As<IPinService>().SingleInstance();
            builder.RegisterType<LoudnessService>().As<ILoudnessService>().SingleInstance();
            builder.RegisterType<TranscodeService>().As<ITranscodeService>().SingleInstance();
            builder.RegisterType<PlaybackMonitorService>().As<IPlaybackMonitorService>().SingleInstance();
            builder.RegisterType<FolderCoverService>().As<IFolderCoverService>().SingleInstance();
            builder.RegisterType<CoverSearchService>().As<ICoverSearchService>().SingleInstance();
            builder.RegisterType<RatingService>().As<IRatingService>().SingleInstance();
            builder.RegisterType<UnplayableService>().As<IUnplayableService>().SingleInstance();
            builder.RegisterType<AbnormalRequestService>().As<IAbnormalRequestService>().SingleInstance();
            builder.RegisterType<VideoConvertService>().As<IVideoConvertService>().SingleInstance();
            builder.RegisterType<NewsService>().As<INewsService>().SingleInstance();
            builder.RegisterType<ReseekFolderJob>().As<IReseekFolderJob>().SingleInstance();

            builder.RegisterInstance(new MemoryCache(new MemoryCacheOptions()));
            container = builder.Build();
            
        }

        public static T Get<T>() where T : class
        {
            if (typeof(T).IsInterface == false)
            {
                throw new Exception("T must be interface");
            }
            return container.Resolve<T>();
        }

        public static T Get<T>(string name) where T : class
        {
            if (typeof(T).IsInterface == false)
            {
                throw new Exception("T must be interface");
            }
            return container.ResolveNamed<T>(name);
        }

        public static SystemConfig GetConfig()
        {
            return container.Resolve<SystemConfig>();
        }

        public static MemoryCache GetCache()
        {
            return container.Resolve<MemoryCache>();
        }
    }
}
