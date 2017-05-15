﻿using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Cache;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.Relators;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;

namespace Umbraco.Core.Persistence.Repositories
{
    /// <summary>
    /// Represents the UserGroupRepository for doing CRUD operations for <see cref="IUserGroup"/>
    /// </summary>
    internal class UserGroupRepository : PetaPocoRepositoryBase<int, IUserGroup>, IUserGroupRepository
    {
        private readonly CacheHelper _cacheHelper;
        private readonly UserGroupWithUsersRepository _userGroupWithUsersRepository;

        public UserGroupRepository(IScopeUnitOfWork work, CacheHelper cacheHelper, ILogger logger, ISqlSyntaxProvider sqlSyntax)
            : base(work, cacheHelper, logger, sqlSyntax)
        {
            _cacheHelper = cacheHelper;
            _userGroupWithUsersRepository = new UserGroupWithUsersRepository(this, work, cacheHelper, logger, sqlSyntax);
        }

        public const string GetByAliasCacheKeyPrefix = "UserGroupRepository_GetByAlias_";
        public static string GetByAliasCacheKey(string alias)
        {
            return GetByAliasCacheKeyPrefix + alias;
        }

        public IUserGroup Get(string alias)
        {
            try
            {
                //need to do a simple query to get the id - put this cache
                var id = IsolatedCache.GetCacheItem<int>(GetByAliasCacheKey(alias), () =>
                {
                    var groupId = Database.ExecuteScalar<int?>("SELECT id FROM umbracoUserGroup WHERE userGroupAlias=@alias", new { alias = alias });
                    if (groupId.HasValue == false) throw new InvalidOperationException("No group found with alias " + alias);
                    return groupId.Value;
                });

                //return from the normal method which will cache
                return Get(id);
            }
            catch (InvalidOperationException)
            {
                //if this is caught it's because we threw this in the caching method
                return null;
            }
        }

        public IEnumerable<IUserGroup> GetGroupsAssignedToSection(string sectionAlias)
        {
            //Here we're building up a query that looks like this, a sub query is required because the resulting structure
            // needs to still contain all of the section rows per user group.

            //SELECT *
            //FROM [umbracoUserGroup]
            //LEFT JOIN [umbracoUserGroup2App]
            //ON [umbracoUserGroup].[id] = [umbracoUserGroup2App].[user]
            //WHERE umbracoUserGroup.id IN (SELECT umbracoUserGroup.id
            //    FROM [umbracoUserGroup]
            //    LEFT JOIN [umbracoUserGroup2App]
            //    ON [umbracoUserGroup].[id] = [umbracoUserGroup2App].[user]
            //    WHERE umbracoUserGroup2App.app = 'content')

            var sql = GetBaseQuery(false);
            var innerSql = GetBaseQuery("umbracoUserGroup.id");
            innerSql.Where("umbracoUserGroup2App.app = " + SqlSyntax.GetQuotedValue(sectionAlias));
            sql.Where(string.Format("umbracoUserGroup.id IN ({0})", innerSql.SQL));
            //must be included for relator to work
            sql.OrderBy<UserGroupDto>(x => x.Id, SqlSyntax);

            return ConvertFromDtos(Database.Fetch<UserGroupDto, UserGroup2AppDto, UserGroupDto>(new UserGroupSectionRelator().Map, sql));
        }

        public void AddOrUpdateGroupWithUsers(IUserGroup userGroup, int[] userIds)
        {
            _userGroupWithUsersRepository.AddOrUpdate(new UserGroupWithUsers(userGroup, userIds));
        }

        
        /// <summary>
        /// Gets the group permissions for the specified entities
        /// </summary>
        /// <param name="groupId">Id of group</param>
        /// <param name="entityIds">Array of entity Ids</param>
        public IEnumerable<EntityPermission> GetPermissionsForEntities(int groupId, params int[] entityIds)
        {
            var repo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper, SqlSyntax);
            return repo.GetPermissionsForEntities(groupId, entityIds);
        }

        /// <summary>
        /// Replaces the same permission set for a single group to any number of entities
        /// </summary>
        /// <param name="groupId">Id of group</param>
        /// <param name="permissions">Permissions as enumerable list of <see cref="char"/></param>
        /// <param name="entityIds">Specify the nodes to replace permissions for. If nothing is specified all permissions are removed.</param>
        public void ReplaceGroupPermissions(int groupId, IEnumerable<char> permissions, params int[] entityIds)
        {
            var repo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper, SqlSyntax);
            repo.ReplacePermissions(groupId, permissions, entityIds);
        }

        /// <summary>
        /// Assigns the same permission set for a single group to any number of entities
        /// </summary>
        /// <param name="groupId">Id of group</param>
        /// <param name="permission">Permissions as enumerable list of <see cref="char"/></param>
        /// <param name="entityIds">Specify the nodes to replace permissions for</param>
        public void AssignGroupPermission(int groupId, char permission, params int[] entityIds)
        {
            var repo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper, SqlSyntax);
            repo.AssignPermission(groupId, permission, entityIds);
        }

        #region Overrides of RepositoryBase<int,IUserType>

        protected override IUserGroup PerformGet(int id)
        {
            var sql = GetBaseQuery(false);
            sql.Where(GetBaseWhereClause(), new { Id = id });
            //must be included for relator to work
            sql.OrderBy<UserGroupDto>(x => x.Id, SqlSyntax);

            var dto = Database.Fetch<UserGroupDto, UserGroup2AppDto, UserGroupDto>(new UserGroupSectionRelator().Map, sql).FirstOrDefault();

            if (dto == null)
                return null;

            var userGroup = UserGroupFactory.BuildEntity(dto);
            return userGroup;
        }

        protected override IEnumerable<IUserGroup> PerformGetAll(params int[] ids)
        {
            var sql = GetBaseQuery(false);

            if (ids.Any())
            {
                sql.Where("umbracoUserGroup.id in (@ids)", new { ids = ids });
            }
            else
            {
                sql.Where<UserGroupDto>(x => x.Id >= 0);
            }
            //must be included for relator to work
            sql.OrderBy<UserGroupDto>(x => x.Id, SqlSyntax);

            var dtos = Database.Fetch<UserGroupDto, UserGroup2AppDto, UserGroupDto>(new UserGroupSectionRelator().Map, sql);
            return ConvertFromDtos(dtos);
        }

        protected override IEnumerable<IUserGroup> PerformGetByQuery(IQuery<IUserGroup> query)
        {
            var sqlClause = GetBaseQuery(false);
            var translator = new SqlTranslator<IUserGroup>(sqlClause, query);
            var sql = translator.Translate();
            //must be included for relator to work
            sql.OrderBy<UserGroupDto>(x => x.Id, SqlSyntax);

            var dtos = Database.Fetch<UserGroupDto, UserGroup2AppDto, UserGroupDto>(new UserGroupSectionRelator().Map, sql);
            return ConvertFromDtos(dtos);
        }

        #endregion

        #region Overrides of PetaPocoRepositoryBase<int,IUserGroup>

        protected override Sql GetBaseQuery(bool isCount)
        {
            var sql = new Sql();
            if (isCount)
            {
                sql.Select("COUNT(*)").From<UserGroupDto>();
            }
            else
            {
                return GetBaseQuery("*");
            }
            return sql;
        }

        protected Sql GetBaseQuery(string columns)
        {
            var sql = new Sql();
            sql.Select(columns)
                .From<UserGroupDto>()
                .LeftJoin<UserGroup2AppDto>()
                .On<UserGroupDto, UserGroup2AppDto>(left => left.Id, right => right.UserGroupId);

            return sql;
        }

        protected override string GetBaseWhereClause()
        {
            return "umbracoUserGroup.id = @Id";
        }

        protected override IEnumerable<string> GetDeleteClauses()
        {
            var list = new List<string>
                           {
                               "DELETE FROM umbracoUser2UserGroup WHERE userGroupId = @Id",
                               "DELETE FROM umbracoUserGroup2App WHERE userGroupId = @Id",
                               "DELETE FROM umbracoUserGroup2NodePermission WHERE userGroupId = @Id",
                               "DELETE FROM umbracoUserGroup WHERE id = @Id"
                           };
            return list;
        }

        protected override Guid NodeObjectTypeId
        {
            get { throw new NotImplementedException(); }
        }

        protected override void PersistNewItem(IUserGroup entity)
        {
            var userGroupDto = UserGroupFactory.BuildDto(entity);

            var id = Convert.ToInt32(Database.Insert(userGroupDto));
            entity.Id = id;

            PersistAllowedSections(entity);
        }

        protected override void PersistUpdatedItem(IUserGroup entity)
        {
            var userGroupDto = UserGroupFactory.BuildDto(entity);

            Database.Update(userGroupDto);

            PersistAllowedSections(entity);
        }

        private void PersistAllowedSections(IUserGroup entity)
        {
            var userGroup = (UserGroup)entity;

            // First delete all 
            Database.Delete<UserGroup2AppDto>("WHERE UserGroupId = @UserGroupId",
                new { UserGroupId = userGroup.Id });

            // Then re-add any associated with the group
            foreach (var app in userGroup.AllowedSections)
            {
                var dto = new UserGroup2AppDto
                {
                    UserGroupId = userGroup.Id,
                    AppAlias = app
                };
                Database.Insert(dto);
            }
        }

        #endregion

        private static IEnumerable<IUserGroup> ConvertFromDtos(IEnumerable<UserGroupDto> dtos)
        {
            return dtos.Select(UserGroupFactory.BuildEntity);
        }

        /// <summary>
        /// used to persist a user group with associated users at once
        /// </summary>
        private class UserGroupWithUsers : Entity, IAggregateRoot
        {
            public UserGroupWithUsers(IUserGroup userGroup, int[] userIds)
            {
                UserGroup = userGroup;
                UserIds = userIds;
            }

            public IUserGroup UserGroup { get; private set; }
            public int[] UserIds { get; private set; }
            
        }

        /// <summary>
        /// used to persist a user group with associated users at once
        /// </summary>
        private class UserGroupWithUsersRepository : PetaPocoRepositoryBase<int, UserGroupWithUsers>
        {
            private readonly UserGroupRepository _userGroupRepo;

            public UserGroupWithUsersRepository(UserGroupRepository userGroupRepo, IScopeUnitOfWork work, CacheHelper cache, ILogger logger, ISqlSyntaxProvider sqlSyntax) 
                : base(work, cache, logger, sqlSyntax)
            {
                _userGroupRepo = userGroupRepo;
            }

            #region Not implemented (don't need to for the purposes of this repo)
            protected override UserGroupWithUsers PerformGet(int id)
            {
                throw new NotImplementedException();
            }
            protected override IEnumerable<UserGroupWithUsers> PerformGetAll(params int[] ids)
            {
                throw new NotImplementedException();
            }
            protected override IEnumerable<UserGroupWithUsers> PerformGetByQuery(IQuery<UserGroupWithUsers> query)
            {
                throw new NotImplementedException();
            }
            protected override Sql GetBaseQuery(bool isCount)
            {
                throw new NotImplementedException();
            }
            protected override string GetBaseWhereClause()
            {
                throw new NotImplementedException();
            }
            protected override IEnumerable<string> GetDeleteClauses()
            {
                throw new NotImplementedException();
            }
            protected override Guid NodeObjectTypeId
            {
                get { throw new NotImplementedException(); }
            } 
            #endregion

            protected override void PersistNewItem(UserGroupWithUsers entity)
            {
                //save the user group
                _userGroupRepo.PersistNewItem(entity.UserGroup);
                if (entity.UserIds != null)
                {
                    //now the user association
                    RemoveAllUsersFromGroup(entity.UserGroup.Id);
                    AddUsersToGroup(entity.UserGroup.Id, entity.UserIds);
                }
                
            }

            protected override void PersistUpdatedItem(UserGroupWithUsers entity)
            {
                //save the user group
                _userGroupRepo.PersistUpdatedItem(entity.UserGroup);
                if (entity.UserIds != null)
                {
                    //now the user association
                    RemoveAllUsersFromGroup(entity.UserGroup.Id);
                    AddUsersToGroup(entity.UserGroup.Id, entity.UserIds);
                }
            }

            /// <summary>
            /// Removes all users from a group
            /// </summary>
            /// <param name="groupId">Id of group</param>
            private void RemoveAllUsersFromGroup(int groupId)
            {
                Database.Delete<User2UserGroupDto>("WHERE userGroupId = @GroupId", new { GroupId = groupId });
            }

            /// <summary>
            /// Adds a set of users to a group
            /// </summary>
            /// <param name="groupId">Id of group</param>
            /// <param name="userIds">Ids of users</param>
            private void AddUsersToGroup(int groupId, int[] userIds)
            {
                //TODO: Check if the user exists?
                foreach (var userId in userIds)
                {
                    var dto = new User2UserGroupDto
                    {
                        UserGroupId = groupId,
                        UserId = userId,
                    };
                    Database.Insert(dto);
                }
            }
        }
    }
}