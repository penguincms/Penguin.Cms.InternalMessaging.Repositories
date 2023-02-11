using Penguin.Cms.Email.Abstractions.Attributes;
using Penguin.Cms.Repositories;
using Penguin.Cms.Security;
using Penguin.Cms.Security.Repositories;
using Penguin.Email.Abstractions.Interfaces;
using Penguin.Email.Templating.Abstractions.Extensions;
using Penguin.Email.Templating.Abstractions.Interfaces;
using Penguin.Extensions.Collections;
using Penguin.Messaging.Core;
using Penguin.Persistence.Abstractions.Interfaces;
using Penguin.Security.Abstractions;
using Penguin.Security.Abstractions.Extensions;
using Penguin.Security.Abstractions.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Penguin.Cms.InternalMessaging.Repositories
{
    public class MessageRepository : EntityRepository<InternalMessage>, IEmailHandler
    {
        protected ISendTemplates EmailTemplateRepository { get; set; }

        protected EntityPermissionsRepository EntityPermissionsRepository { get; set; }

        protected Func<InternalMessage, bool> Filter => (entity) => SecurityProvider.TryCheckAccess(entity);

        protected IRepository<SecurityGroup> SecurityGroupRepository { get; set; }

        protected ISecurityProvider<InternalMessage> SecurityProvider { get; set; }

        protected IUserSession UserSession { get; set; }

        public MessageRepository(IPersistenceContext<InternalMessage> dbContext, EntityPermissionsRepository entityPermissionsRepository, IRepository<SecurityGroup> securityGroupRepository, ISecurityProvider<InternalMessage> securityProvider = null, ISendTemplates emailTemplateRepository = null, IUserSession userSession = null, MessageBus messageBus = null) : base(dbContext, messageBus)
        {
            EntityPermissionsRepository = entityPermissionsRepository;
            SecurityGroupRepository = securityGroupRepository;
            EmailTemplateRepository = emailTemplateRepository;
            UserSession = userSession;
            SecurityProvider = securityProvider;
        }

        public InternalMessage Draft(string Recipient, string Origin = null, int ParentId = 0)
        {
            InternalMessage model = new()
            {
                Parent = new InternalMessage
                {
                    _Id = ParentId
                }
            };

            if (string.IsNullOrWhiteSpace(Origin))
            {
                model.From = UserSession.LoggedInUser.ExternalId;
                model.Origin = UserSession.LoggedInUser.Guid;
            }
            else
            {
                SecurityGroup origin = SecurityGroupRepository.Find(Guid.Parse(Origin));

                model.From = origin.ToString();
                model.Origin = origin.Guid;
            }

            SecurityGroup recipient = SecurityGroupRepository.Find(Guid.Parse(Recipient));
            model.Recipient = recipient?.Guid ?? Guid.Parse(Recipient);
            model.To = recipient?.ToString() ?? Recipient;

            return model;
        }

        public List<InternalMessage> GetByParentId(int parentId)
        {
            return this.Where(n => n.Parent != null && n.Parent._Id == parentId).ToList(Filter);
        }

        public List<InternalMessage> GetByRecipient(SecurityGroup Recipient)
        {
            return Recipient is null ? throw new ArgumentNullException(nameof(Recipient)) : GetByRecipient(Recipient.Guid);
        }

        public List<InternalMessage> GetByRecipient(Guid Recipient)
        {
            List<InternalMessage> topLevel = this.Where(n => n.Recipient == Recipient).Where(Filter).ToList();

            return topLevel;
        }

        public List<InternalMessage> GetBySender(SecurityGroup Sender)
        {
            return Sender is null ? throw new ArgumentNullException(nameof(Sender)) : GetBySender(Sender.Guid);
        }

        public List<InternalMessage> GetBySender(Guid Sender)
        {
            List<InternalMessage> topLevel = this.Where(n => n.Origin == Sender).Where(Filter).ToList();

            return topLevel;
        }

        public InternalMessage GetMessageChain(Guid messageGuid)
        {
            InternalMessage thisMessage = Find(messageGuid);
            InternalMessage message = thisMessage;

            while (thisMessage?.Parent != null)
            {
                thisMessage.Parent = this.Where(m => m._Id == thisMessage.Parent._Id).First();
                thisMessage = thisMessage.Parent;
            }

            return message;
        }

        public List<InternalMessage> GetRootByRecipient(SecurityGroup Recipient, bool Recursive = false)
        {
            IEnumerable<InternalMessage> topLevel = this.Where(n => n.Recipient == Recipient.Guid && n.Parent == null).Where(Filter);

            return Recursive ? topLevel.Select(RecursiveFill).ToList() : topLevel.ToList();
        }

        public List<InternalMessage> GetRootBySender(SecurityGroup Sender, bool Recursive = false)
        {
            IEnumerable<InternalMessage> topLevel = this.Where(n => n.Origin == Sender.Guid && n.Parent == null).Where(Filter);

            return Recursive ? topLevel.Select(RecursiveFill).ToList() : topLevel.ToList();
        }

        public List<InternalMessage> GetRootMenus()
        {
            return this.Where(n => n.Parent == null).ToList().Where(Filter).Select(RecursiveFill).ToList();
        }

        public InternalMessage RecursiveFill(InternalMessage Message)
        {
            ILookup<int, InternalMessage> AllItems = this.ToLookup(k => k.Parent?._Id ?? 0, v => v);

            new List<InternalMessage> { Message }.RecursiveProcess(thisChild =>
            {
                thisChild.Children = AllItems[thisChild._Id].Where(Filter).ToList();

                return thisChild.Children;
            });

            return Message;
        }

        [EmailHandler("Send Message")]
        public InternalMessage SendMessage(InternalMessage toSend, string RecipientEmail)
        {
            if (toSend is null)
            {
                throw new ArgumentNullException(nameof(toSend));
            }

            SecurityGroup Recipient = SecurityGroupRepository.Find(toSend.Recipient);
            SecurityGroup Origin = SecurityGroupRepository.Find(toSend.Origin);

            if (Recipient != null)
            {
                EntityPermissionsRepository.AddPermission(toSend, Recipient, PermissionTypes.Read);
            }

            if (Origin != null)
            {
                EntityPermissionsRepository.AddPermission(toSend, Origin, PermissionTypes.Read);
            }

            AddOrUpdate(toSend);

            EmailTemplateRepository.TrySendTemplate(new Dictionary<string, object>()
            {
                [nameof(toSend)] = toSend,
                [nameof(RecipientEmail)] = RecipientEmail
            });

            return toSend;
        }

        public InternalMessage SendMessage(string Body, string Subject, Guid Recipient, int ParentId = 0, Guid? Origin = null)
        {
            if ((Origin.HasValue ? SecurityGroupRepository.Find(Origin.Value) as ISecurityGroup : UserSession.LoggedInUser) is ISecurityGroup origin)
            {
                SecurityGroup target = SecurityGroupRepository.Find(Recipient);

                InternalMessage toSend = new()
                {
                    Body = Body,
                    Subject = Subject,
                    Recipient = target?.Guid ?? Recipient,
                    Parent = ParentId == 0 ? null : Find(ParentId),
                    Origin = origin.Guid,
                    To = target?.ToString() ?? Recipient.ToString(),
                    From = origin.ToString()
                };

                return SendMessage(toSend, target is User t ? t.Email : string.Empty);
            }
            else
            {
                throw new Exception("Unable to find security group for message.");
            }
        }

        public void SendMessage(string Body, string Subject, SecurityGroup Recipient, int ParentId = 0, SecurityGroup Origin = null)
        {
            if (Recipient is null)
            {
                throw new ArgumentNullException(nameof(Recipient));
            }

            _ = SendMessage(Body, Subject, Recipient.Guid, ParentId, Origin?.Guid);
        }
    }
}