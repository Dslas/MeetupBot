USE [MeetupBot]
GO
/****** Object:  Table [dbo].[Meetups]    Script Date: 22.06.2024 1:36:38 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Meetups](
	[MeetupId] [bigint] NOT NULL,
	[StartTime] [datetime] NOT NULL,
	[EndTime] [datetime] NULL,
	[Participants] [nvarchar](max) NULL,
PRIMARY KEY CLUSTERED 
(
	[MeetupId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
INSERT [dbo].[Meetups] ([MeetupId], [StartTime], [EndTime], [Participants]) VALUES (638545064155427998, CAST(N'2024-06-20T18:53:35.543' AS DateTime), CAST(N'2024-06-20T18:53:40.283' AS DateTime), NULL)
INSERT [dbo].[Meetups] ([MeetupId], [StartTime], [EndTime], [Participants]) VALUES (638546151138669508, CAST(N'2024-06-22T01:05:13.867' AS DateTime), CAST(N'2024-06-22T01:05:15.817' AS DateTime), NULL)
GO
