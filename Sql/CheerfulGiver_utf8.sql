USE [CheerfulGiver]
GO
/****** Object:  Table [dbo].[CGAppSettings]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGAppSettings](
	[SettingKey] [nvarchar](100) NOT NULL,
	[SettingValue] [nvarchar](200) NOT NULL,
	[SettingType] [nvarchar](20) NULL,
	[Description] [nvarchar](500) NULL,
	[UpdatedAt] [datetime2](0) NOT NULL,
	[UpdatedBy] [nvarchar](100) NULL,
 CONSTRAINT [PK_CGAppSettings] PRIMARY KEY CLUSTERED 
(
	[SettingKey] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGCampaigns]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGCampaigns](
	[CampaignRecordId] [int] IDENTITY(1,1) NOT NULL,
	[CampaignName] [nvarchar](200) NOT NULL,
	[StartLocal] [datetime2](0) NOT NULL,
	[EndLocalExclusive] [datetime2](0) NOT NULL,
	[TimeZoneId] [nvarchar](100) NOT NULL,
	[GoalAmount] [decimal](18, 2) NULL,
	[GoalFirstTimeGivers] [int] NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[CreatedBy] [nvarchar](200) NULL,
	[UpdatedAt] [datetime2](7) NOT NULL,
	[UpdatedBy] [nvarchar](200) NULL,
 CONSTRAINT [PK_CGCampaigns] PRIMARY KEY CLUSTERED 
(
	[CampaignRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGChallenges]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGChallenges](
	[ChallengeRecordId] [int] IDENTITY(1,1) NOT NULL,
	[CampaignRecordId] [int] NOT NULL,
	[Name] [nvarchar](200) NOT NULL,
	[ChallengeType] [tinyint] NOT NULL,
	[StartLocal] [datetime2](0) NULL,
	[EndLocalExclusive] [datetime2](0) NULL,
	[Goal] [decimal](18, 2) NOT NULL,
	[SortOrder] [int] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[EligibleMaxGiftAmount] [decimal](18, 2) NULL,
	[PerGiftCap] [decimal](18, 2) NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[CreatedBy] [nvarchar](200) NULL,
	[UpdatedAt] [datetime2](7) NOT NULL,
	[UpdatedBy] [nvarchar](200) NULL,
 CONSTRAINT [PK_CGChallenges] PRIMARY KEY CLUSTERED 
(
	[ChallengeRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGDatesSponsored]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGDatesSponsored](
	[SponsoredDateRecordId] [int] IDENTITY(1,1) NOT NULL,
	[CampaignRecordId] [int] NOT NULL,
	[ConstituentRecordId] [int] NOT NULL,
	[GiftRecordId] [int] NOT NULL,
	[SponsoredDate] [date] NOT NULL,
	[DayPart] [nvarchar](4) NOT NULL,
	[SponsorTier] [nvarchar](20) NULL,
	[RequiredAmount] [decimal](18, 2) NOT NULL,
	[IsCancelled] [bit] NOT NULL,
	[CancelledAt] [datetime2](7) NULL,
	[CancelledBy] [nvarchar](200) NULL,
	[CancelledReason] [nvarchar](400) NULL,
 CONSTRAINT [PK_CGDatesSponsored] PRIMARY KEY CLUSTERED 
(
	[SponsoredDateRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGFirstTimeFundExclusions]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGFirstTimeFundExclusions](
	[FundExclusionId] [int] IDENTITY(1,1) NOT NULL,
	[CampaignRecordId] [int] NOT NULL,
	[FundName] [nvarchar](200) NOT NULL,
	[IsActive] [bit] NOT NULL,
	[SortOrder] [int] NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
 CONSTRAINT [PK_CGFirstTimeFundExclusions] PRIMARY KEY CLUSTERED 
(
	[FundExclusionId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGGiftChallengeMatches]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGGiftChallengeMatches](
	[GiftChallengeMatchRecordId] [int] IDENTITY(1,1) NOT NULL,
	[GiftRecordId] [int] NOT NULL,
	[ChallengeRecordId] [int] NOT NULL,
	[MatchApplied] [decimal](18, 2) NOT NULL,
	[AppliedAt] [datetime2](7) NOT NULL,
 CONSTRAINT [PK_CGGiftChallengeMatches] PRIMARY KEY CLUSTERED 
(
	[GiftChallengeMatchRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [UQ_CGGiftChallengeMatches_Gift_Challenge] UNIQUE NONCLUSTERED 
(
	[GiftRecordId] ASC,
	[ChallengeRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGGifts]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGGifts](
	[GiftRecordId] [int] IDENTITY(1,1) NOT NULL,
	[CampaignRecordId] [int] NOT NULL,
	[ConstituentRecordId] [int] NOT NULL,
	[Amount] [decimal](18, 2) NULL,
	[Method] [nvarchar](20) NULL,
	[Frequency] [nvarchar](20) NULL,
	[Comments] [nvarchar](max) NULL,
	[SponsorshipComments] [nvarchar](max) NULL,
	[NewConstituent] [bit] NOT NULL,
	[EnteringWorkstation] [nvarchar](100) NULL,
	[IsDeleted] [bit] NOT NULL,
	[IsSynchronized] [bit] NOT NULL,
	[SendAcknowlegement] [bit] NOT NULL,
	[EmailAddress] [nvarchar](200) NULL,
	[DonorType] [nvarchar](20) NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[CreatedBy] [nvarchar](200) NULL,
	[IsFirstTimeGiver] [bit] NOT NULL,
	[FirstTimeReason] [nvarchar](200) NULL,
 CONSTRAINT [PK_CGGifts] PRIMARY KEY CLUSTERED 
(
	[GiftRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGGiftWorkflowGifts]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGGiftWorkflowGifts](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[WorkflowId] [uniqueidentifier] NOT NULL,
	[ConstituentId] [int] NOT NULL,
	[Amount] [decimal](18, 2) NOT NULL,
	[Frequency] [nvarchar](30) NULL,
	[Installments] [int] NULL,
	[PledgeDate] [date] NULL,
	[StartDate] [date] NULL,
	[FundId] [nvarchar](50) NULL,
	[CampaignId] [nvarchar](50) NULL,
	[AppealId] [nvarchar](50) NULL,
	[PackageId] [nvarchar](50) NULL,
	[SendReminder] [bit] NOT NULL,
	[Comments] [nvarchar](2000) NULL,
	[ApiAttemptedAtUtc] [datetime2](0) NULL,
	[ApiSucceeded] [bit] NOT NULL,
	[ApiGiftId] [nvarchar](50) NULL,
	[ApiErrorMessage] [nvarchar](2000) NULL,
	[CreatedAtUtc] [datetime2](0) NOT NULL,
 CONSTRAINT [PK_CGGiftWorkflowGifts] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGGiftWorkflows]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGGiftWorkflows](
	[WorkflowId] [uniqueidentifier] NOT NULL,
	[CreatedAtUtc] [datetime2](0) NOT NULL,
	[CompletedAtUtc] [datetime2](0) NULL,
	[MachineName] [nvarchar](128) NOT NULL,
	[WindowsUser] [nvarchar](128) NOT NULL,
	[Status] [nvarchar](30) NOT NULL,
	[SearchText] [nvarchar](200) NULL,
	[ConstituentId] [int] NOT NULL,
	[ConstituentName] [nvarchar](200) NULL,
	[IsFirstTimeGiver] [bit] NULL,
	[IsNewRadioConstituent] [bit] NULL,
	[ContextJson] [nvarchar](max) NOT NULL,
 CONSTRAINT [PK_CGGiftWorkflows] PRIMARY KEY CLUSTERED 
(
	[WorkflowId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGGiftWorkflowSponsorships]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGGiftWorkflowSponsorships](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[WorkflowId] [uniqueidentifier] NOT NULL,
	[ConstituentId] [int] NOT NULL,
	[SponsoredDate] [date] NOT NULL,
	[Slot] [nvarchar](20) NOT NULL,
	[ThresholdAmount] [decimal](18, 2) NULL,
	[CreatedAtUtc] [datetime2](0) NOT NULL,
 CONSTRAINT [PK_CGGiftWorkflowSponsorships] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGOAuthSecrets]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGOAuthSecrets](
	[SecretKey] [nvarchar](128) NOT NULL,
	[AccessTokenEnc] [varbinary](max) NULL,
	[RefreshTokenEnc] [varbinary](max) NULL,
	[ExpiresAtUtc] [datetime2](0) NULL,
	[TokenType] [nvarchar](32) NULL,
	[Scope] [nvarchar](2000) NULL,
	[SubscriptionKeyEnc] [varbinary](max) NULL,
	[UpdatedAtUtc] [datetime2](0) NOT NULL,
	[RowVer] [timestamp] NOT NULL,
	[ModifiedAtUtc] [datetime2](7) NULL,
PRIMARY KEY CLUSTERED 
(
	[SecretKey] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGOperatorCreatedConstituents]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGOperatorCreatedConstituents](
	[CampaignRecordId] [int] NOT NULL,
	[ConstituentRecordId] [int] NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[CreatedBy] [nvarchar](200) NULL,
 CONSTRAINT [PK_CGOperatorCreatedConstituents] PRIMARY KEY CLUSTERED 
(
	[CampaignRecordId] ASC,
	[ConstituentRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[CGSponsorshipConfig]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[CGSponsorshipConfig](
	[CampaignRecordId] [int] NOT NULL,
	[HalfDayMinAmount] [decimal](18, 2) NOT NULL,
	[FullDayMinAmount] [decimal](18, 2) NOT NULL,
	[SponsorshipStartDate] [date] NOT NULL,
	[SponsorshipEndDate] [date] NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[CreatedBy] [nvarchar](200) NULL,
	[UpdatedAt] [datetime2](7) NOT NULL,
	[UpdatedBy] [nvarchar](200) NULL,
 CONSTRAINT [PK_CGSponsorshipConfig] PRIMARY KEY CLUSTERED 
(
	[CampaignRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[REConstituents]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[REConstituents](
	[CnBio_Address] [nvarchar](100) NULL,
	[CnBio_ID] [int] NULL,
	[CnBio_DateAdded] [date] NULL,
	[CnBio_First_Name] [nvarchar](50) NULL,
	[CnBio_Gender] [nvarchar](50) NULL,
	[CnBio_Last_Name] [nvarchar](50) NULL,
	[CnBio_Marital_status] [nvarchar](50) NULL,
	[CnBio_Middle_Name] [nvarchar](50) NULL,
	[CnBio_Name] [nvarchar](150) NULL,
	[CnBio_Nickname] [nvarchar](50) NULL,
	[CnBio_Org_Name] [nvarchar](150) NULL,
	[CnBio_DateAdded2] [date] NULL,
	[CnSpSpBio_ID] [smallint] NULL,
	[CnSpSpBio_First_Name] [nvarchar](50) NULL,
	[CnAdrPrf_Addrline1] [nvarchar](50) NULL,
	[CnAdrPrf_Addrline2] [nvarchar](50) NULL,
	[CnAdrPrf_Addrline3] [nvarchar](50) NULL,
	[CnAdrPrf_Addrline4] [nvarchar](50) NULL,
	[CnAdrPrf_Addrline5] [nvarchar](50) NULL,
	[CnAdrPrf_City] [nvarchar](50) NULL,
	[CnAdrPrf_State] [nvarchar](50) NULL,
	[CnAdrPrf_Type] [nvarchar](50) NULL,
	[CnAdrPrf_ZIP] [nvarchar](50) NULL,
	[CnGf_1_01_Fund] [nvarchar](50) NULL,
	[CnPh_1_01_Phone_number] [nvarchar](50) NULL,
	[CnLGf_1_Campaign] [nvarchar](50) NULL,
	[CnLGf_1_DateAdded] [date] NULL,
	[CnLGf_1_Fund] [nvarchar](50) NULL,
	[CnAttrCat_1_01_Date] [date] NULL,
	[CnAttrCat_2_01_Date] [date] NULL,
	[CnAttrCat_3_01_Date] [date] NULL,
	[CnSmryGft_1_Total_nmbr_of_Gifts] [smallint] NULL,
	[CnSmryGft_1_Total_Gift_amount] [money] NULL,
	[ConstituentRecordId] [int] IDENTITY(1,1) NOT NULL,
 CONSTRAINT [PK_REConstituents] PRIMARY KEY CLUSTERED 
(
	[ConstituentRecordId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
ALTER TABLE [dbo].[CGAppSettings] ADD  CONSTRAINT [DF_CGAppSettings_UpdatedAt]  DEFAULT (sysdatetime()) FOR [UpdatedAt]
GO
ALTER TABLE [dbo].[CGCampaigns] ADD  CONSTRAINT [DF_CGCampaigns_TimeZoneId]  DEFAULT (N'Eastern Standard Time') FOR [TimeZoneId]
GO
ALTER TABLE [dbo].[CGCampaigns] ADD  CONSTRAINT [DF_CGCampaigns_IsActive]  DEFAULT ((0)) FOR [IsActive]
GO
ALTER TABLE [dbo].[CGCampaigns] ADD  CONSTRAINT [DF_CGCampaigns_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[CGCampaigns] ADD  CONSTRAINT [DF_CGCampaigns_UpdatedAt]  DEFAULT (sysutcdatetime()) FOR [UpdatedAt]
GO
ALTER TABLE [dbo].[CGChallenges] ADD  CONSTRAINT [DF_CGChallenges_SortOrder]  DEFAULT ((0)) FOR [SortOrder]
GO
ALTER TABLE [dbo].[CGChallenges] ADD  CONSTRAINT [DF_CGChallenges_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[CGChallenges] ADD  CONSTRAINT [DF_CGChallenges_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[CGChallenges] ADD  CONSTRAINT [DF_CGChallenges_UpdatedAt]  DEFAULT (sysutcdatetime()) FOR [UpdatedAt]
GO
ALTER TABLE [dbo].[CGFirstTimeFundExclusions] ADD  CONSTRAINT [DF_CGFirstTimeFundExclusions_IsActive]  DEFAULT ((1)) FOR [IsActive]
GO
ALTER TABLE [dbo].[CGFirstTimeFundExclusions] ADD  CONSTRAINT [DF_CGFirstTimeFundExclusions_SortOrder]  DEFAULT ((0)) FOR [SortOrder]
GO
ALTER TABLE [dbo].[CGFirstTimeFundExclusions] ADD  CONSTRAINT [DF_CGFirstTimeFundExclusions_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches] ADD  CONSTRAINT [DF_CGGiftChallengeMatches_AppliedAt]  DEFAULT (sysutcdatetime()) FOR [AppliedAt]
GO
ALTER TABLE [dbo].[CGGifts] ADD  CONSTRAINT [DF_CGGifts_NewConstituent]  DEFAULT ((0)) FOR [NewConstituent]
GO
ALTER TABLE [dbo].[CGGifts] ADD  CONSTRAINT [DF_CGGifts_IsDeleted]  DEFAULT ((0)) FOR [IsDeleted]
GO
ALTER TABLE [dbo].[CGGifts] ADD  CONSTRAINT [DF_CGGifts_IsSynchronized]  DEFAULT ((0)) FOR [IsSynchronized]
GO
ALTER TABLE [dbo].[CGGifts] ADD  CONSTRAINT [DF_CGGifts_SendAcknowlegement]  DEFAULT ((0)) FOR [SendAcknowlegement]
GO
ALTER TABLE [dbo].[CGGifts] ADD  CONSTRAINT [DF_CGGifts_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[CGGifts] ADD  CONSTRAINT [DF_CGGifts_IsFirstTimeGiver]  DEFAULT ((0)) FOR [IsFirstTimeGiver]
GO
ALTER TABLE [dbo].[CGGiftWorkflowGifts] ADD  CONSTRAINT [DF_CGGiftWorkflowGifts_SendReminder]  DEFAULT ((0)) FOR [SendReminder]
GO
ALTER TABLE [dbo].[CGGiftWorkflowGifts] ADD  CONSTRAINT [DF_CGGiftWorkflowGifts_ApiSucceeded]  DEFAULT ((0)) FOR [ApiSucceeded]
GO
ALTER TABLE [dbo].[CGGiftWorkflowGifts] ADD  CONSTRAINT [DF_CGGiftWorkflowGifts_CreatedAtUtc]  DEFAULT (sysutcdatetime()) FOR [CreatedAtUtc]
GO
ALTER TABLE [dbo].[CGGiftWorkflowSponsorships] ADD  CONSTRAINT [DF_CGGiftWorkflowSponsorships_CreatedAtUtc]  DEFAULT (sysutcdatetime()) FOR [CreatedAtUtc]
GO
ALTER TABLE [dbo].[CGOAuthSecrets] ADD  CONSTRAINT [DF_CGOAuthSecrets_UpdatedAtUtc]  DEFAULT (sysutcdatetime()) FOR [UpdatedAtUtc]
GO
ALTER TABLE [dbo].[CGOperatorCreatedConstituents] ADD  CONSTRAINT [DF_CGOperatorCreatedConstituents_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[CGSponsorshipConfig] ADD  CONSTRAINT [DF_CGSponsorshipConfig_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [dbo].[CGSponsorshipConfig] ADD  CONSTRAINT [DF_CGSponsorshipConfig_UpdatedAt]  DEFAULT (sysutcdatetime()) FOR [UpdatedAt]
GO
ALTER TABLE [dbo].[CGChallenges]  WITH CHECK ADD  CONSTRAINT [FK_CGChallenges_CGCampaigns] FOREIGN KEY([CampaignRecordId])
REFERENCES [dbo].[CGCampaigns] ([CampaignRecordId])
GO
ALTER TABLE [dbo].[CGChallenges] CHECK CONSTRAINT [FK_CGChallenges_CGCampaigns]
GO
ALTER TABLE [dbo].[CGFirstTimeFundExclusions]  WITH CHECK ADD  CONSTRAINT [FK_CGFirstTimeFundExclusions_CGCampaigns] FOREIGN KEY([CampaignRecordId])
REFERENCES [dbo].[CGCampaigns] ([CampaignRecordId])
GO
ALTER TABLE [dbo].[CGFirstTimeFundExclusions] CHECK CONSTRAINT [FK_CGFirstTimeFundExclusions_CGCampaigns]
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches]  WITH CHECK ADD  CONSTRAINT [FK_CGGiftChallengeMatches_CGChallenges] FOREIGN KEY([ChallengeRecordId])
REFERENCES [dbo].[CGChallenges] ([ChallengeRecordId])
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches] CHECK CONSTRAINT [FK_CGGiftChallengeMatches_CGChallenges]
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches]  WITH CHECK ADD  CONSTRAINT [FK_CGGiftChallengeMatches_CGGifts] FOREIGN KEY([GiftRecordId])
REFERENCES [dbo].[CGGifts] ([GiftRecordId])
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches] CHECK CONSTRAINT [FK_CGGiftChallengeMatches_CGGifts]
GO
ALTER TABLE [dbo].[CGGifts]  WITH CHECK ADD  CONSTRAINT [FK_CGGifts_CGCampaigns] FOREIGN KEY([CampaignRecordId])
REFERENCES [dbo].[CGCampaigns] ([CampaignRecordId])
GO
ALTER TABLE [dbo].[CGGifts] CHECK CONSTRAINT [FK_CGGifts_CGCampaigns]
GO
ALTER TABLE [dbo].[CGGifts]  WITH CHECK ADD  CONSTRAINT [FK_CGGifts_REConstituents] FOREIGN KEY([ConstituentRecordId])
REFERENCES [dbo].[REConstituents] ([ConstituentRecordId])
GO
ALTER TABLE [dbo].[CGGifts] CHECK CONSTRAINT [FK_CGGifts_REConstituents]
GO
ALTER TABLE [dbo].[CGGiftWorkflowGifts]  WITH CHECK ADD  CONSTRAINT [FK_CGGiftWorkflowGifts_Workflow] FOREIGN KEY([WorkflowId])
REFERENCES [dbo].[CGGiftWorkflows] ([WorkflowId])
GO
ALTER TABLE [dbo].[CGGiftWorkflowGifts] CHECK CONSTRAINT [FK_CGGiftWorkflowGifts_Workflow]
GO
ALTER TABLE [dbo].[CGGiftWorkflowSponsorships]  WITH CHECK ADD  CONSTRAINT [FK_CGGiftWorkflowSponsorships_Workflow] FOREIGN KEY([WorkflowId])
REFERENCES [dbo].[CGGiftWorkflows] ([WorkflowId])
GO
ALTER TABLE [dbo].[CGGiftWorkflowSponsorships] CHECK CONSTRAINT [FK_CGGiftWorkflowSponsorships_Workflow]
GO
ALTER TABLE [dbo].[CGOperatorCreatedConstituents]  WITH CHECK ADD  CONSTRAINT [FK_CGOperatorCreatedConstituents_CGCampaigns] FOREIGN KEY([CampaignRecordId])
REFERENCES [dbo].[CGCampaigns] ([CampaignRecordId])
GO
ALTER TABLE [dbo].[CGOperatorCreatedConstituents] CHECK CONSTRAINT [FK_CGOperatorCreatedConstituents_CGCampaigns]
GO
ALTER TABLE [dbo].[CGOperatorCreatedConstituents]  WITH CHECK ADD  CONSTRAINT [FK_CGOperatorCreatedConstituents_REConstituents] FOREIGN KEY([ConstituentRecordId])
REFERENCES [dbo].[REConstituents] ([ConstituentRecordId])
GO
ALTER TABLE [dbo].[CGOperatorCreatedConstituents] CHECK CONSTRAINT [FK_CGOperatorCreatedConstituents_REConstituents]
GO
ALTER TABLE [dbo].[CGSponsorshipConfig]  WITH CHECK ADD  CONSTRAINT [FK_CGSponsorshipConfig_CGCampaigns] FOREIGN KEY([CampaignRecordId])
REFERENCES [dbo].[CGCampaigns] ([CampaignRecordId])
GO
ALTER TABLE [dbo].[CGSponsorshipConfig] CHECK CONSTRAINT [FK_CGSponsorshipConfig_CGCampaigns]
GO
ALTER TABLE [dbo].[CGCampaigns]  WITH CHECK ADD  CONSTRAINT [CK_CGCampaigns_Window] CHECK  (([EndLocalExclusive]>[StartLocal]))
GO
ALTER TABLE [dbo].[CGCampaigns] CHECK CONSTRAINT [CK_CGCampaigns_Window]
GO
ALTER TABLE [dbo].[CGChallenges]  WITH CHECK ADD  CONSTRAINT [CK_CGChallenges_Goal] CHECK  (([Goal]>=(0)))
GO
ALTER TABLE [dbo].[CGChallenges] CHECK CONSTRAINT [CK_CGChallenges_Goal]
GO
ALTER TABLE [dbo].[CGChallenges]  WITH CHECK ADD  CONSTRAINT [CK_CGChallenges_Type] CHECK  (([ChallengeType]=(3) OR [ChallengeType]=(2) OR [ChallengeType]=(1)))
GO
ALTER TABLE [dbo].[CGChallenges] CHECK CONSTRAINT [CK_CGChallenges_Type]
GO
ALTER TABLE [dbo].[CGChallenges]  WITH CHECK ADD  CONSTRAINT [CK_CGChallenges_WindowRules] CHECK  ((([ChallengeType]=(2) OR [ChallengeType]=(1)) AND [StartLocal] IS NOT NULL AND [EndLocalExclusive] IS NOT NULL AND [EndLocalExclusive]>[StartLocal] OR [ChallengeType]=(3) AND [StartLocal] IS NULL AND [EndLocalExclusive] IS NULL))
GO
ALTER TABLE [dbo].[CGChallenges] CHECK CONSTRAINT [CK_CGChallenges_WindowRules]
GO
ALTER TABLE [dbo].[CGDatesSponsored]  WITH CHECK ADD  CONSTRAINT [CK_CGDatesSponsored_CancelFields] CHECK  (([IsCancelled]=(0) AND [CancelledAt] IS NULL AND [CancelledBy] IS NULL AND [CancelledReason] IS NULL OR [IsCancelled]=(1) AND [CancelledAt] IS NOT NULL))
GO
ALTER TABLE [dbo].[CGDatesSponsored] CHECK CONSTRAINT [CK_CGDatesSponsored_CancelFields]
GO
ALTER TABLE [dbo].[CGDatesSponsored]  WITH CHECK ADD  CONSTRAINT [CK_CGDatesSponsored_DayPart] CHECK  (([DayPart]=N'FULL' OR [DayPart]=N'PM' OR [DayPart]=N'AM'))
GO
ALTER TABLE [dbo].[CGDatesSponsored] CHECK CONSTRAINT [CK_CGDatesSponsored_DayPart]
GO
ALTER TABLE [dbo].[CGDatesSponsored]  WITH CHECK ADD  CONSTRAINT [CK_CGDatesSponsored_RequiredAmount] CHECK  (([RequiredAmount]>=(0)))
GO
ALTER TABLE [dbo].[CGDatesSponsored] CHECK CONSTRAINT [CK_CGDatesSponsored_RequiredAmount]
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches]  WITH CHECK ADD  CONSTRAINT [CK_CGGiftChallengeMatches_NonNegative] CHECK  (([MatchApplied]>=(0)))
GO
ALTER TABLE [dbo].[CGGiftChallengeMatches] CHECK CONSTRAINT [CK_CGGiftChallengeMatches_NonNegative]
GO
ALTER TABLE [dbo].[CGGifts]  WITH CHECK ADD  CONSTRAINT [CK_CGGifts_Amount] CHECK  (([Amount] IS NULL OR [Amount]>=(0)))
GO
ALTER TABLE [dbo].[CGGifts] CHECK CONSTRAINT [CK_CGGifts_Amount]
GO
ALTER TABLE [dbo].[CGSponsorshipConfig]  WITH CHECK ADD  CONSTRAINT [CK_CGSponsorshipConfig_Amounts] CHECK  (([HalfDayMinAmount]>=(0) AND [FullDayMinAmount]>=(0) AND [FullDayMinAmount]>=[HalfDayMinAmount]))
GO
ALTER TABLE [dbo].[CGSponsorshipConfig] CHECK CONSTRAINT [CK_CGSponsorshipConfig_Amounts]
GO
ALTER TABLE [dbo].[CGSponsorshipConfig]  WITH CHECK ADD  CONSTRAINT [CK_CGSponsorshipConfig_DateRange] CHECK  (([SponsorshipEndDate]>=[SponsorshipStartDate]))
GO
ALTER TABLE [dbo].[CGSponsorshipConfig] CHECK CONSTRAINT [CK_CGSponsorshipConfig_DateRange]
GO
ALTER TABLE [dbo].[REConstituents]  WITH CHECK ADD  CONSTRAINT [CK_REConstituents_NameRequired] CHECK  ((nullif(ltrim(rtrim([CnBio_Org_Name])),'') IS NOT NULL OR nullif(ltrim(rtrim([CnBio_First_Name])),'') IS NOT NULL OR nullif(ltrim(rtrim([CnBio_Last_Name])),'') IS NOT NULL OR nullif(ltrim(rtrim([CnBio_Name])),'') IS NOT NULL))
GO
ALTER TABLE [dbo].[REConstituents] CHECK CONSTRAINT [CK_REConstituents_NameRequired]
GO
/****** Object:  StoredProcedure [dbo].[CGOAuthSecrets_Get]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[CGOAuthSecrets_Get]
    @SecretKey   NVARCHAR(200),
    @Passphrase  NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        AccessToken =
            CONVERT(NVARCHAR(MAX), DECRYPTBYPASSPHRASE(@Passphrase, AccessTokenEnc)),
        RefreshToken =
            CONVERT(NVARCHAR(MAX), DECRYPTBYPASSPHRASE(@Passphrase, RefreshTokenEnc)),
        ExpiresAtUtc = CONVERT(DATETIME2(0), ExpiresAtUtc),
        TokenType = TokenType,
        Scope = Scope,
        SubscriptionKey =
            CONVERT(NVARCHAR(MAX), DECRYPTBYPASSPHRASE(@Passphrase, SubscriptionKeyEnc))
    FROM dbo.CGOAuthSecrets
    WHERE SecretKey = @SecretKey;
END

GO
/****** Object:  StoredProcedure [dbo].[CGOAuthSecrets_Get2]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ---------------------------------------------------------------------------
   V2 procs: NO PASSPHRASE
   These procs simply read/write the encrypted VARBINARY columns in dbo.CGOAuthSecrets.
   Encryption/decryption happens in the app (DPAPI) via SqlBlackbaudSecretStore DPAPI mode.
--------------------------------------------------------------------------- */

CREATE   PROCEDURE [dbo].[CGOAuthSecrets_Get2]
    @SecretKey NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        AccessTokenEnc,
        RefreshTokenEnc,
        ExpiresAtUtc = CONVERT(DATETIME2(0), ExpiresAtUtc),
        TokenType,
        Scope,
        SubscriptionKeyEnc
    FROM dbo.CGOAuthSecrets
    WHERE SecretKey = @SecretKey;
END

GO
/****** Object:  StoredProcedure [dbo].[CGOAuthSecrets_Upsert]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[CGOAuthSecrets_Upsert]
    @SecretKey        NVARCHAR(200),
    @Passphrase       NVARCHAR(200),
    @AccessToken      NVARCHAR(MAX) = NULL,
    @RefreshToken     NVARCHAR(MAX) = NULL,
    @ExpiresAtUtc     DATETIME2(0)  = NULL,
    @TokenType        NVARCHAR(50)  = NULL,
    @Scope            NVARCHAR(4000)= NULL,
    @SubscriptionKey  NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @SecretKey IS NULL OR LTRIM(RTRIM(@SecretKey)) = ''
        THROW 50000, 'SecretKey is required.', 1;

    IF @Passphrase IS NULL OR LTRIM(RTRIM(@Passphrase)) = ''
        THROW 50000, 'Passphrase is required.', 1;

    IF EXISTS (SELECT 1 FROM dbo.CGOAuthSecrets WHERE SecretKey = @SecretKey)
    BEGIN
        UPDATE dbo.CGOAuthSecrets
        SET
            AccessTokenEnc =
                CASE WHEN @AccessToken IS NULL THEN AccessTokenEnc
                     ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @AccessToken) END,

            RefreshTokenEnc =
                CASE WHEN @RefreshToken IS NULL THEN RefreshTokenEnc
                     ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @RefreshToken) END,

            ExpiresAtUtc = COALESCE(@ExpiresAtUtc, ExpiresAtUtc),

            TokenType = COALESCE(@TokenType, TokenType),
            Scope     = COALESCE(@Scope, Scope),

            SubscriptionKeyEnc =
                CASE WHEN @SubscriptionKey IS NULL THEN SubscriptionKeyEnc
                     ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @SubscriptionKey) END,

            UpdatedAtUtc = SYSUTCDATETIME()
        WHERE SecretKey = @SecretKey;
    END
    ELSE
    BEGIN
        INSERT dbo.CGOAuthSecrets
        (
            SecretKey,
            AccessTokenEnc,
            RefreshTokenEnc,
            ExpiresAtUtc,
            TokenType,
            Scope,
            SubscriptionKeyEnc
        )
        VALUES
        (
            @SecretKey,
            CASE WHEN @AccessToken IS NULL THEN NULL ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @AccessToken) END,
            CASE WHEN @RefreshToken IS NULL THEN NULL ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @RefreshToken) END,
            @ExpiresAtUtc,
            @TokenType,
            @Scope,
            CASE WHEN @SubscriptionKey IS NULL THEN NULL ELSE ENCRYPTBYPASSPHRASE(@Passphrase, @SubscriptionKey) END
        );
    END
END

GO
/****** Object:  StoredProcedure [dbo].[CGOAuthSecrets_Upsert2]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE   PROCEDURE [dbo].[CGOAuthSecrets_Upsert2]
    @SecretKey          NVARCHAR(128),
    @AccessTokenEnc     VARBINARY(MAX) = NULL,
    @RefreshTokenEnc    VARBINARY(MAX) = NULL,
    @ExpiresAtUtc       DATETIME2(0)   = NULL,
    @TokenType          NVARCHAR(50)   = NULL,
    @Scope              NVARCHAR(4000) = NULL,
    @SubscriptionKeyEnc VARBINARY(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @SecretKey IS NULL OR LTRIM(RTRIM(@SecretKey)) = ''
        THROW 50000, 'SecretKey is required.', 1;

    IF EXISTS (SELECT 1 FROM dbo.CGOAuthSecrets WHERE SecretKey = @SecretKey)
    BEGIN
        UPDATE dbo.CGOAuthSecrets
        SET
            AccessTokenEnc     = COALESCE(@AccessTokenEnc, AccessTokenEnc),
            RefreshTokenEnc    = COALESCE(@RefreshTokenEnc, RefreshTokenEnc),
            ExpiresAtUtc       = COALESCE(@ExpiresAtUtc, ExpiresAtUtc),
            TokenType          = COALESCE(@TokenType, TokenType),
            Scope              = COALESCE(@Scope, Scope),
            SubscriptionKeyEnc = COALESCE(@SubscriptionKeyEnc, SubscriptionKeyEnc),
            UpdatedAtUtc       = SYSUTCDATETIME(),
            ModifiedAtUtc      = SYSUTCDATETIME()
        WHERE SecretKey = @SecretKey;
    END
    ELSE
    BEGIN
        INSERT dbo.CGOAuthSecrets
        (
            SecretKey,
            AccessTokenEnc,
            RefreshTokenEnc,
            ExpiresAtUtc,
            TokenType,
            Scope,
            SubscriptionKeyEnc,
            ModifiedAtUtc
        )
        VALUES
        (
            @SecretKey,
            @AccessTokenEnc,
            @RefreshTokenEnc,
            @ExpiresAtUtc,
            @TokenType,
            @Scope,
            @SubscriptionKeyEnc,
            SYSUTCDATETIME()
        );
    END
END

GO
/****** Object:  StoredProcedure [dbo].[usp_REConstituents_Insert_WithDupeGuard]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/* ============================================================
   7) RECREATE DUPLICATE-GUARD PROCS FOR REConstituents
   ============================================================ */

CREATE PROCEDURE [dbo].[usp_REConstituents_Insert_WithDupeGuard]
    @Name   NVARCHAR(200),
    @Org    NVARCHAR(200) = NULL,
    @Spouse NVARCHAR(200) = NULL,
    @Addr1  NVARCHAR(200) = NULL,
    @Addr2  NVARCHAR(200) = NULL,
    @City   NVARCHAR(200) = NULL,
    @State  NVARCHAR(50)  = NULL,
    @Zip    NVARCHAR(20)  = NULL,
    @Phone  NVARCHAR(50)  = NULL,
    @AllowDuplicate BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE
        @NameN   NVARCHAR(200) = LTRIM(RTRIM(COALESCE(@Name,  N''))),
        @Addr1N  NVARCHAR(200) = LTRIM(RTRIM(COALESCE(@Addr1, N''))),
        @CityN   NVARCHAR(200) = LTRIM(RTRIM(COALESCE(@City,  N''))),
        @StateN  NVARCHAR(50)  = LTRIM(RTRIM(COALESCE(@State, N''))),
        @ZipN    NVARCHAR(20)  = LTRIM(RTRIM(COALESCE(@Zip,   N''))),
        @PhoneD  NVARCHAR(50)  = dbo.fn_DigitsOnly(@Phone);

    IF @NameN = N''
        THROW 51010, 'Name is required.', 1;

    BEGIN TRAN;

    DECLARE @lockResult INT;
    EXEC @lockResult = sp_getapplock
        @Resource = 'REConstituents_DupeGuard',
        @LockMode = 'Exclusive',
        @LockOwner = 'Transaction',
        @LockTimeout = 10000;

    IF @lockResult < 0
    BEGIN
        ROLLBACK;
        THROW 51011, 'Could not obtain duplicate-guard lock. Please try again.', 1;
    END

    IF (@AllowDuplicate = 0)
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM dbo.REConstituents r WITH (UPDLOCK, HOLDLOCK)
            WHERE
                (
                    (LEN(@PhoneD) >= 7 AND dbo.fn_DigitsOnly(r.CnPh_1_01_Phone_number) LIKE N'%' + @PhoneD + N'%')
                    OR
                    (LEN(@ZipN) >= 5 AND r.CnAdrPrf_ZIP LIKE @ZipN + N'%' AND r.CnBio_Name LIKE N'%' + @NameN + N'%')
                    OR
                    (LEN(@Addr1N) >= 4 AND r.CnBio_Name LIKE N'%' + @NameN + N'%' AND r.CnAdrPrf_Addrline1 LIKE N'%' + @Addr1N + N'%')
                )
        )
        BEGIN
            ROLLBACK;
            THROW 51001,
                'Server duplicate guardrail: A likely duplicate exists (phone/name/address/ZIP match). Use the existing constituent or enable Admin Override if absolutely necessary.',
                1;
        END
    END

    INSERT INTO dbo.REConstituents
    (
        CnBio_Name,
        CnBio_Org_Name,
        CnSpSpBio_First_Name,
        CnAdrPrf_Addrline1,
        CnAdrPrf_Addrline2,
        CnAdrPrf_City,
        CnAdrPrf_State,
        CnAdrPrf_ZIP,
        CnPh_1_01_Phone_number
    )
    VALUES
    (
        @NameN,
        NULLIF(LTRIM(RTRIM(@Org)), N''),
        NULLIF(LTRIM(RTRIM(@Spouse)), N''),
        NULLIF(@Addr1N, N''),
        NULLIF(LTRIM(RTRIM(@Addr2)), N''),
        NULLIF(@CityN, N''),
        NULLIF(@StateN, N''),
        NULLIF(@ZipN, N''),
        NULLIF(LTRIM(RTRIM(@Phone)), N'')
    );

    DECLARE @newId INT = CAST(SCOPE_IDENTITY() AS INT);

    COMMIT;
    SELECT @newId AS ConstituentRecordId;
END;
GO
/****** Object:  StoredProcedure [dbo].[usp_REConstituents_Update_WithDupeGuard]    Script Date: 2/21/2026 5:22:56 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[usp_REConstituents_Update_WithDupeGuard]
    @ConstituentRecordId INT,
    @Name   NVARCHAR(200),
    @Org    NVARCHAR(200) = NULL,
    @Spouse NVARCHAR(200) = NULL,
    @Addr1  NVARCHAR(200) = NULL,
    @Addr2  NVARCHAR(200) = NULL,
    @City   NVARCHAR(200) = NULL,
    @State  NVARCHAR(50)  = NULL,
    @Zip    NVARCHAR(20)  = NULL,
    @Phone  NVARCHAR(50)  = NULL,
    @AllowDuplicate BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE
        @NameN   NVARCHAR(200) = LTRIM(RTRIM(COALESCE(@Name,  N''))),
        @Addr1N  NVARCHAR(200) = LTRIM(RTRIM(COALESCE(@Addr1, N''))),
        @CityN   NVARCHAR(200) = LTRIM(RTRIM(COALESCE(@City,  N''))),
        @StateN  NVARCHAR(50)  = LTRIM(RTRIM(COALESCE(@State, N''))),
        @ZipN    NVARCHAR(20)  = LTRIM(RTRIM(COALESCE(@Zip,   N''))),
        @PhoneD  NVARCHAR(50)  = dbo.fn_DigitsOnly(@Phone);

    IF @NameN = N''
        THROW 51010, 'Name is required.', 1;

    BEGIN TRAN;

    DECLARE @lockResult INT;
    EXEC @lockResult = sp_getapplock
        @Resource = 'REConstituents_DupeGuard',
        @LockMode = 'Exclusive',
        @LockOwner = 'Transaction',
        @LockTimeout = 10000;

    IF @lockResult < 0
    BEGIN
        ROLLBACK;
        THROW 51011, 'Could not obtain duplicate-guard lock. Please try again.', 1;
    END

    IF (@AllowDuplicate = 0)
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM dbo.REConstituents r WITH (UPDLOCK, HOLDLOCK)
            WHERE r.ConstituentRecordId <> @ConstituentRecordId
              AND
              (
                    (LEN(@PhoneD) >= 7 AND dbo.fn_DigitsOnly(r.CnPh_1_01_Phone_number) LIKE N'%' + @PhoneD + N'%')
                    OR
                    (LEN(@ZipN) >= 5 AND r.CnAdrPrf_ZIP LIKE @ZipN + N'%' AND r.CnBio_Name LIKE N'%' + @NameN + N'%')
                    OR
                    (LEN(@Addr1N) >= 4 AND r.CnBio_Name LIKE N'%' + @NameN + N'%' AND r.CnAdrPrf_Addrline1 LIKE N'%' + @Addr1N + N'%')
              )
        )
        BEGIN
            ROLLBACK;
            THROW 51002,
                'Server duplicate guardrail: Saving these edits would likely duplicate another constituent (phone/name/address/ZIP match). Review matches or use Admin Override if absolutely necessary.',
                1;
        END
    END

    UPDATE dbo.REConstituents
    SET
        CnBio_Name = @NameN,
        CnBio_Org_Name = NULLIF(LTRIM(RTRIM(@Org)), N''),
        CnSpSpBio_First_Name = NULLIF(LTRIM(RTRIM(@Spouse)), N''),
        CnAdrPrf_Addrline1 = NULLIF(@Addr1N, N''),
        CnAdrPrf_Addrline2 = NULLIF(LTRIM(RTRIM(@Addr2)), N''),
        CnAdrPrf_City = NULLIF(@CityN, N''),
        CnAdrPrf_State = NULLIF(@StateN, N''),
        CnAdrPrf_ZIP = NULLIF(@ZipN, N''),
        CnPh_1_01_Phone_number = NULLIF(LTRIM(RTRIM(@Phone)), N'')
    WHERE ConstituentRecordId = @ConstituentRecordId;

    COMMIT;
END;
GO
