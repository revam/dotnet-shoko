﻿using System.Collections.Generic;
using JMMServer.Entities;
using NHibernate;
using NHibernate.Criterion;

namespace JMMServer.Repositories
{
    public class CrossRef_AniDB_Trakt_EpisodeRepository
    {
        public void Save(CrossRef_AniDB_Trakt_Episode obj)
        {
            using (var session = JMMService.SessionFactory.OpenSession())
            {
                // populate the database
                using (var transaction = session.BeginTransaction())
                {
                    session.SaveOrUpdate(obj);
                    transaction.Commit();
                }
            }
        }

        public CrossRef_AniDB_Trakt_Episode GetByID(int id)
        {
            using (var session = JMMService.SessionFactory.OpenSession())
            {
                return session.Get<CrossRef_AniDB_Trakt_Episode>(id);
            }
        }

        public CrossRef_AniDB_Trakt_Episode GetByAniDBEpisodeID(int id)
        {
            using (var session = JMMService.SessionFactory.OpenSession())
            {
                CrossRef_AniDB_Trakt_Episode cr = session
                    .CreateCriteria(typeof(CrossRef_AniDB_Trakt_Episode))
                    .Add(Restrictions.Eq("AniDBEpisodeID", id))
                    .UniqueResult<CrossRef_AniDB_Trakt_Episode>();
                return cr;
            }
        }

        public List<CrossRef_AniDB_Trakt_Episode> GetByAnimeID(int id)
        {
            using (var session = JMMService.SessionFactory.OpenSession())
            {
                return GetByAnimeID(session, id);
            }
        }

        public List<CrossRef_AniDB_Trakt_Episode> GetByAnimeID(ISession session, int id)
        {
            var objs = session
                .CreateCriteria(typeof(CrossRef_AniDB_Trakt_Episode))
                .Add(Restrictions.Eq("AnimeID", id))
                .List<CrossRef_AniDB_Trakt_Episode>();

            return new List<CrossRef_AniDB_Trakt_Episode>(objs);
        }

        public List<CrossRef_AniDB_Trakt_Episode> GetAll()
        {
            using (var session = JMMService.SessionFactory.OpenSession())
            {
                var series = session
                    .CreateCriteria(typeof(CrossRef_AniDB_Trakt_Episode))
                    .List<CrossRef_AniDB_Trakt_Episode>();

                return new List<CrossRef_AniDB_Trakt_Episode>(series);
            }
        }

        public void Delete(int id)
        {
            using (var session = JMMService.SessionFactory.OpenSession())
            {
                // populate the database
                using (var transaction = session.BeginTransaction())
                {
                    CrossRef_AniDB_Trakt_Episode cr = GetByID(id);
                    if (cr != null)
                    {
                        session.Delete(cr);
                        transaction.Commit();
                    }
                }
            }
        }
    }
}