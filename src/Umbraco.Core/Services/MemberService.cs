﻿using System;
using System.Collections.Generic;
using System.Web.Security;
using Umbraco.Core.Events;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.UnitOfWork;
using System.Linq;
using Umbraco.Core.Composing;
using Umbraco.Core.Exceptions;
using Umbraco.Core.IO;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Security;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the MemberService.
    /// </summary>
    public class MemberService : ScopeRepositoryService, IMemberService
    {
        private readonly IMemberGroupService _memberGroupService;
        private readonly MediaFileSystem _mediaFileSystem;

        //only for unit tests!
        internal MembershipProviderBase MembershipProvider { get; set; }

        #region Constructor

        public MemberService(IScopeUnitOfWorkProvider provider, ILogger logger, IEventMessagesFactory eventMessagesFactory, IMemberGroupService memberGroupService,  MediaFileSystem mediaFileSystem)
            : base(provider, logger, eventMessagesFactory)
        {
            _memberGroupService = memberGroupService ?? throw new ArgumentNullException(nameof(memberGroupService));
            _mediaFileSystem = mediaFileSystem ?? throw new ArgumentNullException(nameof(mediaFileSystem));
        }

        #endregion

        #region Count

        /// <summary>
        /// Gets the total number of Members based on the count type
        /// </summary>
        /// <remarks>
        /// The way the Online count is done is the same way that it is done in the MS SqlMembershipProvider - We query for any members
        /// that have their last active date within the Membership.UserIsOnlineTimeWindow (which is in minutes). It isn't exact science
        /// but that is how MS have made theirs so we'll follow that principal.
        /// </remarks>
        /// <param name="countType"><see cref="MemberCountType"/> to count by</param>
        /// <returns><see cref="System.int"/> with number of Members for passed in type</returns>
        public int GetCount(MemberCountType countType)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                IQuery<IMember> query;

                switch (countType)
                {
                    case MemberCountType.All:
                        query = Query<IMember>();
                        break;
                    case MemberCountType.Online:
                        var fromDate = DateTime.Now.AddMinutes(-Membership.UserIsOnlineTimeWindow);
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.LastLoginDate &&
                            ((Member)x).DateTimePropertyValue > fromDate);
                        break;
                    case MemberCountType.LockedOut:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.IsLockedOut &&
                            ((Member)x).BoolPropertyValue);
                        break;
                    case MemberCountType.Approved:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.IsApproved &&
                            ((Member)x).BoolPropertyValue);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(countType));
                }

                return repository.GetCountByQuery(query);
            }
        }

        /// <summary>
        /// Gets the count of Members by an optional MemberType alias
        /// </summary>
        /// <remarks>If no alias is supplied then the count for all Member will be returned</remarks>
        /// <param name="memberTypeAlias">Optional alias for the MemberType when counting number of Members</param>
        /// <returns><see cref="System.int"/> with number of Members</returns>
        public int Count(string memberTypeAlias = null)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.Count(memberTypeAlias);
            }
        }

        #endregion

        #region Create

        /// <summary>
        /// Creates an <see cref="IMember"/> object without persisting it
        /// </summary>
        /// <remarks>This method is convenient for when you need to add properties to a new Member
        /// before persisting it in order to limit the amount of times its saved.
        /// Also note that the returned <see cref="IMember"/> will not have an Id until its saved.</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberTypeAlias">Alias of the MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMember(string username, string email, string name, string memberTypeAlias)
        {
            var memberType = GetMemberType(memberTypeAlias);
            if (memberType == null)
                throw new ArgumentException("No member type with that alias.", nameof(memberTypeAlias));

            var member = new Member(name, email.ToLower().Trim(), username, memberType);
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                CreateMember(uow, member, 0, false);
                uow.Complete();
            }

            return member;
        }

        /// <summary>
        /// Creates an <see cref="IMember"/> object without persisting it
        /// </summary>
        /// <remarks>This method is convenient for when you need to add properties to a new Member
        /// before persisting it in order to limit the amount of times its saved.
        /// Also note that the returned <see cref="IMember"/> will not have an Id until its saved.</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMember(string username, string email, string name, IMemberType memberType)
        {
            if (memberType == null) throw new ArgumentNullException(nameof(memberType));

            var member = new Member(name, email.ToLower().Trim(), username, memberType);
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                CreateMember(uow, member, 0, false);
                uow.Complete();
            }

            return member;
        }

        /// <summary>
        /// Creates and persists a new <see cref="IMember"/>
        /// </summary>
        /// <remarks>An <see cref="IMembershipUser"/> can be of type <see cref="IMember"/> or <see cref="IUser"/></remarks>
        /// <param name="username">Username of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="email">Email of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="passwordValue">This value should be the encoded/encrypted/hashed value for the password that will be stored in the database</param>
        /// <param name="memberTypeAlias">Alias of the Type</param>
        /// <param name="isApproved">Is the member approved</param>
        /// <returns><see cref="IMember"/></returns>
        IMember IMembershipMemberService<IMember>.CreateWithIdentity(string username, string email, string passwordValue, string memberTypeAlias)
        {
            return CreateMemberWithIdentity(username, email, username, passwordValue, memberTypeAlias);
        }

        /// <summary>
        /// Creates and persists a new <see cref="IMember"/>
        /// </summary>
        /// <remarks>An <see cref="IMembershipUser"/> can be of type <see cref="IMember"/> or <see cref="IUser"/></remarks>
        /// <param name="username">Username of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="email">Email of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="passwordValue">This value should be the encoded/encrypted/hashed value for the password that will be stored in the database</param>
        /// <param name="memberTypeAlias">Alias of the Type</param>
        /// <returns><see cref="IMember"/></returns>
        IMember IMembershipMemberService<IMember>.CreateWithIdentity(string username, string email, string passwordValue, string memberTypeAlias, bool isApproved = true)
        {
            return CreateMemberWithIdentity(username, email, username, passwordValue, memberTypeAlias, isApproved);
        }

        public IMember CreateMemberWithIdentity(string username, string email, string memberTypeAlias)
        {
            return CreateMemberWithIdentity(username, email, username, "", memberTypeAlias);
        }

        public IMember CreateMemberWithIdentity(string username, string email, string memberTypeAlias, bool isApproved)
        {
            return CreateMemberWithIdentity(username, email, username, "", memberTypeAlias, isApproved);
        }

        public IMember CreateMemberWithIdentity(string username, string email, string name, string memberTypeAlias)
        {
            return CreateMemberWithIdentity(username, email, name, "", memberTypeAlias);
        }

        public IMember CreateMemberWithIdentity(string username, string email, string name, string memberTypeAlias, bool isApproved)
        {
            return CreateMemberWithIdentity(username, email, name, "", memberTypeAlias, isApproved);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberTypeAlias">Alias of the MemberType the Member should be based on</param>
        /// <param name="isApproved">Optional IsApproved of the Member to create</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, string name, string passwordValue, string memberTypeAlias, bool isApproved = true)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                // locking the member tree secures member types too
                uow.WriteLock(Constants.Locks.MemberTree);

                var memberType = GetMemberType(uow, memberTypeAlias); // + locks
                if (memberType == null)
                    throw new ArgumentException("No member type with that alias.", nameof(memberTypeAlias)); // causes rollback

                var member = new Member(name, email.ToLower().Trim(), username, passwordValue, memberType, isApproved);
                CreateMember(uow, member, -1, true);

                uow.Complete();
                return member;
            }
        }

        public IMember CreateMemberWithIdentity(string username, string email, IMemberType memberType)
        {
            return CreateMemberWithIdentity(username, email, username, "", memberType);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, IMemberType memberType, bool isApproved)
        {
            return CreateMemberWithIdentity(username, email, username, "", memberType, isApproved);
        }

        public IMember CreateMemberWithIdentity(string username, string email, string name, IMemberType memberType)
        {
            return CreateMemberWithIdentity(username, email, name, "", memberType);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, string name, IMemberType memberType, bool isApproved)
        {
            return CreateMemberWithIdentity(username, email, name, "", memberType, isApproved);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="passwordValue">This value should be the encoded/encrypted/hashed value for the password that will be stored in the database</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        private IMember CreateMemberWithIdentity(string username, string email, string name, string passwordValue, IMemberType memberType, bool isApproved = true)
        {
            if (memberType == null) throw new ArgumentNullException(nameof(memberType));

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);

                // ensure it all still make sense
                var vrfy = GetMemberType(uow, memberType.Alias); // + locks
                if (vrfy == null || vrfy.Id != memberType.Id)
                    throw new ArgumentException($"Member type with alias {memberType.Alias} does not exist or is a different member type."); // causes rollback

                var member = new Member(name, email.ToLower().Trim(), username, passwordValue, memberType, isApproved);
                CreateMember(uow, member, -1, true);

                uow.Complete();
                return member;
            }
        }

        private void CreateMember(IScopeUnitOfWork uow, Member member, int userId, bool withIdentity)
        {
            // there's no Creating event for members

            member.CreatorId = userId;

            if (withIdentity)
            {
                var saveEventArgs = new SaveEventArgs<IMember>(member);
                if (uow.Events.DispatchCancelable(Saving, this, saveEventArgs))
                {
                    member.WasCancelled = true;
                    return;
                }

                var repository = uow.CreateRepository<IMemberRepository>();
                repository.AddOrUpdate(member);

                saveEventArgs.CanCancel = false;
                uow.Events.Dispatch(Saved, this, saveEventArgs);
            }

            uow.Events.Dispatch(Created, this, new NewEventArgs<IMember>(member, false, member.ContentType.Alias, -1));

            var msg = withIdentity
                ? "Member '{0}' was created with Id {1}"
                : "Member '{0}' was created";
            Audit(uow, AuditType.New, string.Format(msg, member.Name, member.Id), member.CreatorId, member.Id);
        }

        #endregion

        #region Get, Has, Is, Exists...

        /// <summary>
        /// Gets a Member by its integer id
        /// </summary>
        /// <param name="id"><see cref="System.int"/> Id</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetById(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.Get(id);
            }
        }

        /// <summary>
        /// Gets a Member by the unique key
        /// </summary>
        /// <remarks>The guid key corresponds to the unique id in the database
        /// and the user id in the membership provider.</remarks>
        /// <param name="id"><see cref="Guid"/> Id</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByKey(Guid id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>().Where(x => x.Key == id);
                return repository.GetByQuery(query).FirstOrDefault();
            }
        }

        /// <summary>
        /// Gets a list of paged <see cref="IMember"/> objects
        /// </summary>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.GetPagedResultsByQuery(null, pageIndex, pageSize, out totalRecords, "LoginName", Direction.Ascending, true);
            }
        }

        // fixme get rid of string filter?

        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, string memberTypeAlias = null, string filter = "")
        {
            return GetAll(pageIndex, pageSize, out totalRecords, orderBy, orderDirection, true, memberTypeAlias, filter);
        }

        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, bool orderBySystemField, string memberTypeAlias, string filter)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query1 = memberTypeAlias == null ? null : Query<IMember>().Where(x => x.ContentTypeAlias == memberTypeAlias);
                var query2 = filter == null ? null : Query<IMember>().Where(x => x.Name.Contains(filter) || x.Username.Contains(filter));
                return repository.GetPagedResultsByQuery(query1, pageIndex, pageSize, out totalRecords, orderBy, orderDirection, orderBySystemField, query2);
            }
        }

        /// <summary>
        /// Gets an <see cref="IMember"/> by its provider key
        /// </summary>
        /// <param name="id">Id to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByProviderKey(object id)
        {
            var asGuid = id.TryConvertTo<Guid>();
            if (asGuid.Success)
                return GetByKey(asGuid.Result);

            var asInt = id.TryConvertTo<int>();
            if (asInt.Success)
                return GetById(asInt.Result);

            return null;
        }

        /// <summary>
        /// Get an <see cref="IMember"/> by email
        /// </summary>
        /// <param name="email">Email to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByEmail(string email)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>().Where(x => x.Email.Equals(email));
                return repository.GetByQuery(query).FirstOrDefault();
            }
        }

        /// <summary>
        /// Get an <see cref="IMember"/> by username
        /// </summary>
        /// <param name="username">Username to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByUsername(string username)
        {
            //TODO: Somewhere in here, whether at this level or the repository level, we need to add
            // a caching mechanism since this method is used by all the membership providers and could be
            // called quite a bit when dealing with members.

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>().Where(x => x.Username.Equals(username));
                return repository.GetByQuery(query).FirstOrDefault();
            }
        }

        /// <summary>
        /// Gets all Members for the specified MemberType alias
        /// </summary>
        /// <param name="memberTypeAlias">Alias of the MemberType</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByMemberType(string memberTypeAlias)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>().Where(x => x.ContentTypeAlias == memberTypeAlias);
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets all Members for the MemberType id
        /// </summary>
        /// <param name="memberTypeId">Id of the MemberType</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByMemberType(int memberTypeId)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                repository.Get(memberTypeId);
                var query = Query<IMember>().Where(x => x.ContentTypeId == memberTypeId);
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets all Members within the specified MemberGroup name
        /// </summary>
        /// <param name="memberGroupName">Name of the MemberGroup</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByGroup(string memberGroupName)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.GetByMemberGroup(memberGroupName);
            }
        }

        /// <summary>
        /// Gets all Members with the ids specified
        /// </summary>
        /// <remarks>If no Ids are specified all Members will be retrieved</remarks>
        /// <param name="ids">Optional list of Member Ids</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetAllMembers(params int[] ids)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.GetAll(ids);
            }
        }

        /// <summary>
        /// Finds Members based on their display name
        /// </summary>
        /// <param name="displayNameToMatch">Display name to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindMembersByDisplayName(string displayNameToMatch, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>();

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query.Where(member => member.Name.Equals(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.Contains:
                        query.Where(member => member.Name.Contains(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query.Where(member => member.Name.StartsWith(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query.Where(member => member.Name.EndsWith(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.Wildcard:
                        query.Where(member => member.Name.SqlWildcard(displayNameToMatch, TextColumnType.NVarchar));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(matchType)); // causes rollback
                }

                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, "Name", Direction.Ascending, true);
            }
        }

        /// <summary>
        /// Finds a list of <see cref="IMember"/> objects by a partial email string
        /// </summary>
        /// <param name="emailStringToMatch">Partial email string to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindByEmail(string emailStringToMatch, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>();

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query.Where(member => member.Email.Equals(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.Contains:
                        query.Where(member => member.Email.Contains(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query.Where(member => member.Email.StartsWith(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query.Where(member => member.Email.EndsWith(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.Wildcard:
                        query.Where(member => member.Email.SqlWildcard(emailStringToMatch, TextColumnType.NVarchar));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(matchType));
                }

                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, "Email", Direction.Ascending, true);
            }
        }

        /// <summary>
        /// Finds a list of <see cref="IMember"/> objects by a partial username
        /// </summary>
        /// <param name="login">Partial username to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindByUsername(string login, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>();

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query.Where(member => member.Username.Equals(login));
                        break;
                    case StringPropertyMatchType.Contains:
                        query.Where(member => member.Username.Contains(login));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query.Where(member => member.Username.StartsWith(login));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query.Where(member => member.Username.EndsWith(login));
                        break;
                    case StringPropertyMatchType.Wildcard:
                        query.Where(member => member.Email.SqlWildcard(login, TextColumnType.NVarchar));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(matchType));
                }

                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, "LoginName", Direction.Ascending, true);
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.string"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, string value, StringPropertyMatchType matchType = StringPropertyMatchType.Exact)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                IQuery<IMember> query;

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            (((Member)x).LongStringPropertyValue.SqlEquals(value, TextColumnType.NText) ||
                                ((Member)x).ShortStringPropertyValue.SqlEquals(value, TextColumnType.NVarchar)));
                        break;
                    case StringPropertyMatchType.Contains:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            (((Member)x).LongStringPropertyValue.SqlContains(value, TextColumnType.NText) ||
                                ((Member)x).ShortStringPropertyValue.SqlContains(value, TextColumnType.NVarchar)));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            (((Member)x).LongStringPropertyValue.SqlStartsWith(value, TextColumnType.NText) ||
                                ((Member)x).ShortStringPropertyValue.SqlStartsWith(value, TextColumnType.NVarchar)));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            (((Member)x).LongStringPropertyValue.SqlEndsWith(value, TextColumnType.NText) ||
                                ((Member)x).ShortStringPropertyValue.SqlEndsWith(value, TextColumnType.NVarchar)));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(matchType));
                }

                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.int"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, int value, ValuePropertyMatchType matchType = ValuePropertyMatchType.Exact)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                IQuery<IMember> query;

                switch (matchType)
                {
                    case ValuePropertyMatchType.Exact:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).IntegerPropertyValue == value);
                        break;
                    case ValuePropertyMatchType.GreaterThan:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).IntegerPropertyValue > value);
                        break;
                    case ValuePropertyMatchType.LessThan:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).IntegerPropertyValue < value);
                        break;
                    case ValuePropertyMatchType.GreaterThanOrEqualTo:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).IntegerPropertyValue >= value);
                        break;
                    case ValuePropertyMatchType.LessThanOrEqualTo:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).IntegerPropertyValue <= value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(matchType));
                }

                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.bool"/> Value to match</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, bool value)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                var query = Query<IMember>().Where(x =>
                    ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                    ((Member)x).BoolPropertyValue == value);

                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.DateTime"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, DateTime value, ValuePropertyMatchType matchType = ValuePropertyMatchType.Exact)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                IQuery<IMember> query;

                switch (matchType)
                {
                    case ValuePropertyMatchType.Exact:
                        query = Query<IMember>().Where( x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).DateTimePropertyValue == value);
                        break;
                    case ValuePropertyMatchType.GreaterThan:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).DateTimePropertyValue > value);
                        break;
                    case ValuePropertyMatchType.LessThan:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).DateTimePropertyValue < value);
                        break;
                    case ValuePropertyMatchType.GreaterThanOrEqualTo:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).DateTimePropertyValue >= value);
                        break;
                    case ValuePropertyMatchType.LessThanOrEqualTo:
                        query = Query<IMember>().Where(x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).DateTimePropertyValue <= value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(matchType)); // causes rollback
                }

                //TODO: Since this is by property value, we need a GetByPropertyQuery on the repo!
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Checks if a Member with the id exists
        /// </summary>
        /// <param name="id">Id of the Member</param>
        /// <returns><c>True</c> if the Member exists otherwise <c>False</c></returns>
        public bool Exists(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.Exists(id);
            }
        }

        /// <summary>
        /// Checks if a Member with the username exists
        /// </summary>
        /// <param name="username">Username to check</param>
        /// <returns><c>True</c> if the Member exists otherwise <c>False</c></returns>
        public bool Exists(string username)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.Exists(username);
            }
        }

        #endregion

        #region Save

        /// <summary>
        /// Saves an <see cref="IMember"/>
        /// </summary>
        /// <param name="member"><see cref="IMember"/> to Save</param>
        /// <param name="raiseEvents">Optional parameter to raise events.
        /// Default is <c>True</c> otherwise set to <c>False</c> to not raise events</param>
        public void Save(IMember member, bool raiseEvents = true)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var saveEventArgs = new SaveEventArgs<IMember>(member);
                if (raiseEvents && uow.Events.DispatchCancelable(Saving, this, saveEventArgs))
                {
                    uow.Complete();
                    return;
                }

                if (string.IsNullOrWhiteSpace(member.Name))
                {
                    throw new ArgumentException("Cannot save member with empty name.");
                }

                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();

                repository.AddOrUpdate(member);

                if (raiseEvents)
                {
                    saveEventArgs.CanCancel = false;
                    uow.Events.Dispatch(Saved, this, saveEventArgs);
                }
                Audit(uow, AuditType.Save, "Save Member performed by user", 0, member.Id);

                uow.Complete();
            }
        }

        /// <summary>
        /// Saves a list of <see cref="IMember"/> objects
        /// </summary>
        /// <param name="members"><see cref="IEnumerable{IMember}"/> to save</param>
        /// <param name="raiseEvents">Optional parameter to raise events.
        /// Default is <c>True</c> otherwise set to <c>False</c> to not raise events</param>
        public void Save(IEnumerable<IMember> members, bool raiseEvents = true)
        {
            var membersA = members.ToArray();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var saveEventArgs = new SaveEventArgs<IMember>(membersA);
                if (raiseEvents && uow.Events.DispatchCancelable(Saving, this, saveEventArgs))
                {
                    uow.Complete();
                    return;
                }

                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                foreach (var member in membersA)
                    repository.AddOrUpdate(member);

                if (raiseEvents)
                {
                    saveEventArgs.CanCancel = false;
                    uow.Events.Dispatch(Saved, this, saveEventArgs);
                }
                Audit(uow, AuditType.Save, "Save Member items performed by user", 0, -1);

                uow.Complete();
            }
        }

        #endregion

        #region Delete

        /// <summary>
        /// Deletes an <see cref="IMember"/>
        /// </summary>
        /// <param name="member"><see cref="IMember"/> to Delete</param>
        public void Delete(IMember member)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var deleteEventArgs = new DeleteEventArgs<IMember>(member);
                if (uow.Events.DispatchCancelable(Deleting, this, deleteEventArgs))
                {
                    uow.Complete();
                    return;
                }

                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                DeleteLocked(uow, repository, member, deleteEventArgs);

                Audit(uow, AuditType.Delete, "Delete Member performed by user", 0, member.Id);
                uow.Complete();
            }
        }

        private void DeleteLocked(IScopeUnitOfWork uow, IMemberRepository repository, IMember member, DeleteEventArgs<IMember> args = null)
        {
            // a member has no descendants
            repository.Delete(member);
            if (args == null)
                args = new DeleteEventArgs<IMember>(member, false); // raise event & get flagged files
            else
                args.CanCancel = false;
            uow.Events.Dispatch(Deleted, this, args);

            // fixme - this is MOOT because the event will not trigger immediately
            // it's been refactored already (think it's the dispatcher that deals with it?)
            _mediaFileSystem.DeleteFiles(args.MediaFilesToDelete, // remove flagged files
                (file, e) => Logger.Error<MemberService>("An error occurred while deleting file attached to nodes: " + file, e));
        }

        #endregion

        #region Roles

        public void AddRole(string roleName)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                repository.CreateIfNotExists(roleName);
                uow.Complete();
            }
        }

        public IEnumerable<string> GetAllRoles()
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                return repository.GetAll().Select(x => x.Name).Distinct();
            }
        }

        public IEnumerable<string> GetAllRoles(int memberId)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                var result = repository.GetMemberGroupsForMember(memberId);
                return result.Select(x => x.Name).Distinct();
            }
        }

        public IEnumerable<string> GetAllRoles(string username)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                var result = repository.GetMemberGroupsForMember(username);
                return result.Select(x => x.Name).Distinct();
            }
        }

        public IEnumerable<IMember> GetMembersInRole(string roleName)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.GetByMemberGroup(roleName);
            }
        }

        public IEnumerable<IMember> FindMembersInRole(string roleName, string usernameToMatch, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();
                return repository.FindMembersInRole(roleName, usernameToMatch, matchType);
            }
        }

        public bool DeleteRole(string roleName, bool throwIfBeingUsed)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();

                if (throwIfBeingUsed)
                {
                    // get members in role
                    var memberRepository = uow.CreateRepository<IMemberRepository>();
                    var membersInRole = memberRepository.GetByMemberGroup(roleName);
                    if (membersInRole.Any())
                        throw new InvalidOperationException("The role " + roleName + " is currently assigned to members");
                }

                var query = Query<IMemberGroup>().Where(g => g.Name == roleName);
                var found = repository.GetByQuery(query).ToArray();

                foreach (var memberGroup in found)
                    _memberGroupService.Delete(memberGroup);

                uow.Complete();
                return found.Length > 0;
            }
        }

        public void AssignRole(string username, string roleName)
        {
            AssignRoles(new[] { username }, new[] { roleName });
        }

        public void AssignRoles(string[] usernames, string[] roleNames)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                repository.AssignRoles(usernames, roleNames);
                uow.Complete();
            }
        }

        public void DissociateRole(string username, string roleName)
        {
            DissociateRoles(new[] { username }, new[] { roleName });
        }

        public void DissociateRoles(string[] usernames, string[] roleNames)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                repository.DissociateRoles(usernames, roleNames);
                uow.Complete();
            }
        }

        public void AssignRole(int memberId, string roleName)
        {
            AssignRoles(new[] { memberId }, new[] { roleName });
        }

        public void AssignRoles(int[] memberIds, string[] roleNames)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                repository.AssignRoles(memberIds, roleNames);
                uow.Complete();
            }
        }

        public void DissociateRole(int memberId, string roleName)
        {
            DissociateRoles(new[] { memberId }, new[] { roleName });
        }

        public void DissociateRoles(int[] memberIds, string[] roleNames)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberGroupRepository>();
                repository.DissociateRoles(memberIds, roleNames);
                uow.Complete();
            }
        }

        #endregion

        #region Private Methods

        private void Audit(IUnitOfWork uow, AuditType type, string message, int userId, int objectId)
        {
            var repo = uow.CreateRepository<IAuditRepository>();
            repo.AddOrUpdate(new AuditItem(objectId, message, type, userId));
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Occurs before Delete
        /// </summary>
        public static event TypedEventHandler<IMemberService, DeleteEventArgs<IMember>> Deleting;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IMemberService, DeleteEventArgs<IMember>> Deleted;

        /// <summary>
        /// Occurs before Save
        /// </summary>
        public static event TypedEventHandler<IMemberService, SaveEventArgs<IMember>> Saving;

        /// <summary>
        /// Occurs after Create
        /// </summary>
        /// <remarks>
        /// Please note that the Member object has been created, but might not have been saved
        /// so it does not have an identity yet (meaning no Id has been set).
        /// </remarks>
        public static event TypedEventHandler<IMemberService, NewEventArgs<IMember>> Created;

        /// <summary>
        /// Occurs after Save
        /// </summary>
        public static event TypedEventHandler<IMemberService, SaveEventArgs<IMember>> Saved;

        #endregion

        #region Membership

        /// <summary>
        /// This is simply a helper method which essentially just wraps the MembershipProvider's ChangePassword method
        /// </summary>
        /// <remarks>This method exists so that Umbraco developers can use one entry point to create/update
        /// Members if they choose to. </remarks>
        /// <param name="member">The Member to save the password for</param>
        /// <param name="password">The password to encrypt and save</param>
        public void SavePassword(IMember member, string password)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));

            var provider = MembershipProvider ?? MembershipProviderExtensions.GetMembersMembershipProvider();
            if (provider.IsUmbracoMembershipProvider())
                provider.ChangePassword(member.Username, "", password); // this is actually updating the password
            else
                throw new NotSupportedException("When using a non-Umbraco membership provider you must change the member password by using the MembershipProvider.ChangePassword method");

            // go re-fetch the member to update the properties that may have changed
            // check that it still exists (optimistic concurrency somehow)

            // re-fetch and ensure it exists
            var m = GetByUsername(member.Username);
            if (m == null) return; // gone

            // update properties that have changed
            member.RawPasswordValue = m.RawPasswordValue;
            member.LastPasswordChangeDate = m.LastPasswordChangeDate;
            member.UpdateDate = m.UpdateDate;

            // no need to save anything - provider.ChangePassword has done the updates,
            // and then all we do is re-fetch to get the updated values, and update the
            // in-memory member accordingly
        }

        /// <summary>
        /// A helper method that will create a basic/generic member for use with a generic membership provider
        /// </summary>
        /// <returns></returns>
        internal static IMember CreateGenericMembershipProviderMember(string name, string email, string username, string password)
        {
            var identity = int.MaxValue;

            var memType = new MemberType(-1);
            var propGroup = new PropertyGroup
            {
                Name = "Membership",
                Id = --identity
            };
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TextboxAlias, DataTypeDatabaseType.Ntext, Constants.Conventions.Member.Comments)
            {
                Name = Constants.Conventions.Member.CommentsLabel,
                SortOrder = 0,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TrueFalseAlias, DataTypeDatabaseType.Integer, Constants.Conventions.Member.IsApproved)
            {
                Name = Constants.Conventions.Member.IsApprovedLabel,
                SortOrder = 3,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TrueFalseAlias, DataTypeDatabaseType.Integer, Constants.Conventions.Member.IsLockedOut)
            {
                Name = Constants.Conventions.Member.IsLockedOutLabel,
                SortOrder = 4,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastLockoutDate)
            {
                Name = Constants.Conventions.Member.LastLockoutDateLabel,
                SortOrder = 5,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastLoginDate)
            {
                Name = Constants.Conventions.Member.LastLoginDateLabel,
                SortOrder = 6,
                Id = --identity,
                Key = identity.ToGuid()
            });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastPasswordChangeDate)
            {
                Name = Constants.Conventions.Member.LastPasswordChangeDateLabel,
                SortOrder = 7,
                Id = --identity,
                Key = identity.ToGuid()
            });

            memType.PropertyGroups.Add(propGroup);

            // should we "create member"?
            var member = new Member(name, email, username, password, memType);

            //we've assigned ids to the property types and groups but we also need to assign fake ids to the properties themselves.
            foreach (var property in member.Properties)
            {
                property.Id = --identity;
            }

            return member;
        }

        #endregion

        #region Content Types

        /// <summary>
        /// Delete Members of the specified MemberType id
        /// </summary>
        /// <param name="memberTypeId">Id of the MemberType</param>
        public void DeleteMembersOfType(int memberTypeId)
        {
            // note: no tree to manage here

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.MemberTree);
                var repository = uow.CreateRepository<IMemberRepository>();

                //TODO: What about content that has the contenttype as part of its composition?
                var query = Query<IMember>().Where(x => x.ContentTypeId == memberTypeId);
                var members = repository.GetByQuery(query).ToArray();

                var deleteEventArgs = new DeleteEventArgs<IMember>(members);
                if (uow.Events.DispatchCancelable(Deleting, this, deleteEventArgs))
                {
                    uow.Complete();
                    return;
                }

                foreach (var member in members)
                {
                    // delete media
                    // triggers the deleted event (and handles the files)
                    DeleteLocked(uow, repository, member);
                }

                uow.Complete();
            }
        }

        private IMemberType GetMemberType(IScopeUnitOfWork uow, string memberTypeAlias)
        {
            if (string.IsNullOrWhiteSpace(memberTypeAlias)) throw new ArgumentNullOrEmptyException(nameof(memberTypeAlias));

            uow.ReadLock(Constants.Locks.MemberTypes);

            var repository = uow.CreateRepository<IMemberTypeRepository>();
            var memberType = repository.Get(memberTypeAlias);

            if (memberType == null)
                throw new Exception($"No MemberType matching the passed in Alias: '{memberTypeAlias}' was found"); // causes rollback

            return memberType;
        }

        private IMemberType GetMemberType(string memberTypeAlias)
        {
            if (string.IsNullOrWhiteSpace(memberTypeAlias)) throw new ArgumentNullOrEmptyException(nameof(memberTypeAlias));

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                return GetMemberType(uow, memberTypeAlias);
            }
        }

        // fixme - this should not be here, or???
        public string GetDefaultMemberType()
        {
            return Current.Services.MemberTypeService.GetDefault();
        }

        #endregion
    }
}
