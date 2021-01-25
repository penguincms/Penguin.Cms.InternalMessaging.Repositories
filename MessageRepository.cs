using Penguin.Cms.Email.Abstractions.Attributes;
using Penguin.Cms.InternalMessaging;
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
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Penguin.Cms.Modules.InternalMessaging.Repositories
{

    public class MessageRepository : EntityRepository<InternalMessage>, IEmailHandler
    {
        protected ISendTemplates EmailTemplateRepository { get; set; }

        protected EntityPermissionsRepository EntityPermissionsRepository { get; set; }

        protected Func<InternalMessage, bool> Filter => (entity) =>
        {
            return this.SecurityProvider.TryCheckAccess(entity);
        };

        protected IRepository<SecurityGroup> SecurityGroupRepository { get; set; }
        protected ISecurityProvider<InternalMessage> SecurityProvider { get; set; }
        protected IUserSession UserSession { get; set; }

        public MessageRepository(IPersistenceContext<InternalMessage> dbContext, EntityPermissionsRepository entityPermissionsRepository, IRepository<SecurityGroup> securityGroupRepository, ISecurityProvider<InternalMessage> securityProvider = null, ISendTemplates emailTemplateRepository = null, IUserSession userSession = null, MessageBus messageBus = null) : base(dbContext, messageBus)
        {
            this.EntityPermissionsRepository = entityPermissionsRepository;
            this.SecurityGroupRepository = securityGroupRepository;
            this.EmailTemplateRepository = emailTemplateRepository;
            this.UserSession = userSession;
            this.SecurityProvider = securityProvider;
        }

        public InternalMessage Draft(string Recipient, string Origin = null, int ParentId = 0)
        {
            InternalMessage model = new InternalMessage
            {
                Parent = new InternalMessage
                {
                    _Id = ParentId
                }
            };

            if (string.IsNullOrWhiteSpace(Origin))
            {
                model.From = this.UserSession.LoggedInUser.ExternalId;
                model.Origin = this.UserSession.LoggedInUser.Guid;
            }
            else
            {
                SecurityGroup origin = this.SecurityGroupRepository.Find(Guid.Parse(Origin));

                model.From = origin.ToString();
                model.Origin = origin.Guid;
            }

            SecurityGroup recipient = this.SecurityGroupRepository.Find(Guid.Parse(Recipient));
            model.Recipient = recipient?.Guid ?? Guid.Parse(Recipient);
            model.To = recipient?.ToString() ?? Recipient;

            return model;
        }

        public List<InternalMessage> GetByParentId(int parentId)
        {
            return this.Where(n => n.Parent != null && n.Parent._Id == parentId).ToList(this.Filter);
        }

        public List<InternalMessage> GetByRecipient(SecurityGroup Recipient)
        {
            if (Recipient is null)
            {
                throw new ArgumentNullException(nameof(Recipient));
            }

            return this.GetByRecipient(Recipient.Guid);
        }

        public List<InternalMessage> GetByRecipient(Guid Recipient)
        {


            List<InternalMessage> topLevel = this.Where(n => n.Recipient == Recipient).Where(this.Filter).ToList();

            return topLevel;
        }

        public List<InternalMessage> GetBySender(SecurityGroup Sender)
        {
            if (Sender is null)
            {
                throw new ArgumentNullException(nameof(Sender));
            }

            return this.GetBySender(Sender.Guid);
        }

        public List<InternalMessage> GetBySender(Guid Sender)
        {
            List<InternalMessage> topLevel = this.Where(n => n.Origin == Sender).Where(this.Filter).ToList();

            return topLevel;
        }

        public InternalMessage GetMessageChain(Guid messageGuid)
        {
            InternalMessage thisMessage = this.Find(messageGuid);
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
            IEnumerable<InternalMessage> topLevel = this.Where(n => n.Recipient == Recipient.Guid && n.Parent == null).Where(this.Filter);

            if (Recursive)
            {
                return topLevel.Select(m => this.RecursiveFill(m)).ToList();
            }
            else
            {
                return topLevel.ToList();
            }
        }

        public List<InternalMessage> GetRootBySender(SecurityGroup Sender, bool Recursive = false)
        {
            IEnumerable<InternalMessage> topLevel = this.Where(n => n.Origin == Sender.Guid && n.Parent == null).Where(this.Filter);

            if (Recursive)
            {
                return topLevel.Select(m => this.RecursiveFill(m)).ToList();
            }
            else
            {
                return topLevel.ToList();
            }
        }

        public List<InternalMessage> GetRootMenus()
        {
            return this.Where(n => n.Parent == null).ToList().Where(this.Filter).Select(n => this.RecursiveFill(n)).ToList();
        }

        public InternalMessage RecursiveFill(InternalMessage Message)
        {
            ILookup<int, InternalMessage> AllItems = this.ToLookup(k => k.Parent?._Id ?? 0, v => v);

            new List<InternalMessage> { Message }.RecursiveProcess(thisChild =>
            {
                thisChild.Children = AllItems[thisChild._Id].Where(this.Filter).ToList();

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

            SecurityGroup Recipient = this.SecurityGroupRepository.Find(toSend.Recipient);
            SecurityGroup Origin = this.SecurityGroupRepository.Find(toSend.Origin);

            if (Recipient != null)
            {
                this.EntityPermissionsRepository.AddPermission(toSend, Recipient, PermissionTypes.Read);
            }

            if (Origin != null)
            {
                this.EntityPermissionsRepository.AddPermission(toSend, Origin, PermissionTypes.Read);
            }

            this.AddOrUpdate(toSend);

            this.EmailTemplateRepository.TrySendTemplate(new Dictionary<string, object>()
            {
                [nameof(toSend)] = toSend,
                [nameof(RecipientEmail)] = RecipientEmail
            });

            return toSend;
        }

        public InternalMessage SendMessage(string Body, string Subject, Guid Recipient, int ParentId = 0, Guid? Origin = null)
        {
            if ((Origin.HasValue ? this.SecurityGroupRepository.Find(Origin.Value) as ISecurityGroup : this.UserSession.LoggedInUser) is ISecurityGroup origin)
            {
                SecurityGroup target = this.SecurityGroupRepository.Find(Recipient);

                InternalMessage toSend = new InternalMessage()
                {
                    Body = Body,
                    Subject = Subject,
                    Recipient = target?.Guid ?? Recipient,
                    Parent = ParentId == 0 ? null : this.Find(ParentId),
                    Origin = origin.Guid,
                    To = target?.ToString() ?? Recipient.ToString(),
                    From = origin.ToString()
                };

                return this.SendMessage(toSend, target is User t ? t.Email : string.Empty);
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

            this.SendMessage(Body, Subject, Recipient.Guid, ParentId, Origin?.Guid);
        }
    }
}