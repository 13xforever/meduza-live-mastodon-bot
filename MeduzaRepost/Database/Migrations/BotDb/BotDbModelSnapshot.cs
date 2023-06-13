﻿// <auto-generated />
using System;
using MeduzaRepost.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace MeduzaRepost.Database.Migrations.BotDb
{
    [DbContext(typeof(Database.BotDb))]
    partial class BotDbModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "7.0.5");

            modelBuilder.Entity("MeduzaRepost.Database.BotState", b =>
                {
                    b.Property<string>("Key")
                        .HasColumnType("TEXT");

                    b.Property<string>("Value")
                        .HasColumnType("TEXT");

                    b.HasKey("Key");

                    b.ToTable("BotState");
                });

            modelBuilder.Entity("MeduzaRepost.Database.MessageMap", b =>
                {
                    b.Property<long>("TelegramId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("MastodonId")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<long?>("Pts")
                        .HasColumnType("INTEGER");

                    b.HasKey("TelegramId");

                    b.HasIndex("MastodonId")
                        .IsUnique();

                    b.ToTable("MessageMaps");
                });
#pragma warning restore 612, 618
        }
    }
}
