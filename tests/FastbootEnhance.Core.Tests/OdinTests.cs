using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FastbootEnhance.Core.Samsung.Odin;

namespace FastbootEnhance.Core.Tests
{
    public class OdinTests
    {
        // ---------------------------------------------------------------- packets

        [Fact]
        public void A_command_is_1024_bytes_of_little_endian_integers()
        {
            byte[] packet = OdinPacket.Command(0x65, 2, 7);
            Assert.Equal(1024, packet.Length);
            Assert.Equal(new byte[] { 0x65, 0, 0, 0, 2, 0, 0, 0, 7, 0, 0, 0 }, packet.Take(12).ToArray());
            Assert.All(packet.Skip(12), b => Assert.Equal(0, b));
        }

        [Fact]
        public void An_answer_of_another_type_or_0xFF_is_refused()
        {
            byte[] ok = new byte[8];
            OdinPacket.PutInt(ok, 0, 0x64);
            OdinPacket.PutInt(ok, 4, 0x40000);
            Assert.Equal(0x40000, OdinPacket.ResponseValue(ok, 0x64, "x"));

            Assert.Throws<OdinException>(() => OdinPacket.ResponseValue(ok, 0x65, "x"));

            byte[] refused = new byte[8];
            OdinPacket.PutInt(refused, 0, 0xFF);
            OdinPacket.PutInt(refused, 4, -5);
            OdinException e = Assert.Throws<OdinException>(() => OdinPacket.ResponseValue(refused, 0x64, "x"));
            Assert.Equal(-5, e.PhoneCode);
            Assert.Throws<OdinException>(() => OdinPacket.ResponseValue(new byte[4], 0x64, "x"));
        }

        [Fact]
        public void Text_stops_at_zero_and_hides_what_is_not_printable()
        {
            byte[] field = { (byte)'B', (byte)'O', 0x1B, (byte)'T', 0, (byte)'X' };
            Assert.Equal("BO?T", OdinPacket.Text(field, 0, field.Length));
            Assert.Equal("BO", OdinPacket.Text(field, 0, 2));
        }

        // ---------------------------------------------------------------- PIT

        [Fact]
        public void The_sample_PIT_reads_back_as_it_was_written()
        {
            SamsungPit pit = SamsungPit.Parse(FakeOdinDevice.SamplePit());
            Assert.Equal("COM_TAR2", pit.Gang);
            Assert.Equal("SM8550", pit.Project);
            Assert.True(pit.IsVersion2);
            Assert.Equal(18, pit.Entries.Count);

            PitEntry boot = pit.ForFile("BOOT.IMG");
            Assert.NotNull(boot);
            Assert.Equal("BOOT", boot.Partition);
            Assert.Equal(12, boot.PartitionId);
            Assert.Equal(66560, boot.BlockSizeOrStart);
            Assert.Equal(32768, boot.BlockCount);
            Assert.False(boot.IsModem);
            Assert.True(pit.ForPartition("radio").IsModem);
            Assert.Null(pit.ForFile("nothing.img"));
            Assert.Null(pit.ForFile(""));
        }

        [Fact]
        public void A_name_of_32_characters_or_more_is_cut_to_fit_its_field()
        {
            string longName = new string('A', 40);
            byte[] data = SamsungPit.Build("COM_TAR2", "", new[] { SamsungPit.Entry(1, longName, "a.img", 0, 1) });
            Assert.Equal(new string('A', 31), SamsungPit.Parse(data).Entries[0].Partition);
        }

        [Fact]
        public void A_PIT_of_one_block_size_is_version_1()
        {
            byte[] data = SamsungPit.Build("COM_TAR2", "", new[]
            {
                SamsungPit.Entry(1, "BOOT", "boot.img", 512, 10),
                SamsungPit.Entry(2, "RECOVERY", "recovery.img", 512, 20),
            });
            Assert.False(SamsungPit.Parse(data).IsVersion2);
        }

        [Fact]
        public void What_is_not_a_PIT_or_is_cut_short_is_refused()
        {
            Assert.False(SamsungPit.LooksLikePit(null));
            Assert.False(SamsungPit.LooksLikePit(new byte[28]));
            Assert.Throws<InvalidOperationException>(() => SamsungPit.Parse(new byte[100]));

            byte[] data = FakeOdinDevice.SamplePit();
            Assert.Throws<InvalidOperationException>(() => SamsungPit.Parse(data.Take(data.Length - 1).ToArray()));

            byte[] lying = (byte[])data.Clone();
            OdinPacket.PutInt(lying, 4, -1);
            Assert.Throws<InvalidOperationException>(() => SamsungPit.Parse(lying));
            OdinPacket.PutInt(lying, 4, int.MaxValue);
            Assert.Throws<InvalidOperationException>(() => SamsungPit.Parse(lying));
        }

        // ---------------------------------------------------------------- device information

        [Fact]
        public void Device_information_is_read_by_type()
        {
            OdinDeviceInfo info = OdinDeviceInfo.Parse(FakeOdinDevice.SampleDeviceInfo());
            Assert.Equal("SM-S911B", info.Model);
            Assert.Equal("R5CW00EXAMPLE", info.Serial);
            Assert.Equal("EUX", info.SalesCode);
            Assert.Equal("EUX", info.CarrierId);
        }

        [Fact]
        public void Device_information_whose_locations_point_at_the_text_is_read_too()
        {
            byte[] text = Encoding.ASCII.GetBytes("SM-A546B");
            byte[] data = new byte[8 + 12 + text.Length + 4];
            OdinPacket.PutInt(data, 0, OdinDeviceInfo.Magic);
            OdinPacket.PutInt(data, 4, 1);
            OdinPacket.PutInt(data, 8, 0);
            OdinPacket.PutInt(data, 12, 20);
            OdinPacket.PutInt(data, 16, text.Length);
            text.CopyTo(data, 20);
            Assert.Equal("SM-A546B", OdinDeviceInfo.Parse(data).Model);
        }

        [Fact]
        public void Device_information_that_is_broken_gives_nothing_rather_than_a_crash()
        {
            Assert.Null(OdinDeviceInfo.Parse(null));
            Assert.Null(OdinDeviceInfo.Parse(new byte[500]));

            byte[] data = FakeOdinDevice.SampleDeviceInfo();
            byte[] tooMany = (byte[])data.Clone();
            OdinPacket.PutInt(tooMany, 4, 1000);
            Assert.Null(OdinDeviceInfo.Parse(tooMany));

            // A location past the end is skipped; the others are still read.
            byte[] badOffset = (byte[])data.Clone();
            OdinPacket.PutInt(badOffset, 8 + 4, int.MaxValue);
            OdinDeviceInfo info = OdinDeviceInfo.Parse(badOffset);
            Assert.Null(info.Model);
            Assert.Equal("R5CW00EXAMPLE", info.Serial);

            byte[] negativeSize = (byte[])data.Clone();
            OdinPacket.PutInt(negativeSize, 8 + 8, -1);
            Assert.Null(OdinDeviceInfo.Parse(negativeSize).Model);
        }

        // ---------------------------------------------------------------- a reading

        [Fact]
        public void A_reading_gets_the_PIT_and_the_device_information_then_restarts_the_phone()
        {
            FakeOdinDevice phone = new FakeOdinDevice();
            List<string> log = new List<string>();
            OdinReading reading = OdinReader.Read(phone, OdinReboot.Normal, log.Add);

            Assert.Equal(4, reading.ProtocolVersion);
            Assert.True(reading.TakesLz4);
            Assert.Equal(FakeOdinDevice.SamplePit(), reading.PitData);
            Assert.Equal(18, reading.Pit.Entries.Count);
            Assert.Equal("SM-S911B", reading.Info.Model);
            Assert.Null(reading.InfoProblem);
            Assert.True(reading.Ended);
            Assert.Equal("reboot", phone.Restarted);

            int pitBlocks = (phone.Pit.Length + 499) / 500;
            int infoBlocks = (phone.DeviceInfo.Length + 499) / 500;
            List<string> expected = new List<string> { "0x64/0", "0x65/1" };
            expected.AddRange(Enumerable.Repeat("0x65/2", pitBlocks));
            expected.Add("0x65/3");
            expected.Add("0x69/0");
            expected.AddRange(Enumerable.Repeat("0x69/1", infoBlocks));
            expected.AddRange(new[] { "0x69/2", "0x67/0", "0x67/1" });
            Assert.Equal(expected, phone.Received);
            Assert.Equal(phone.Received.Count, phone.EmptyPackets);
        }

        [Fact]
        public void Nothing_but_reading_packets_is_ever_sent()
        {
            FakeOdinDevice phone = new FakeOdinDevice();
            OdinReader.Read(phone, OdinReboot.Download);
            string[] reading = { "0x64/0", "0x65/1", "0x65/2", "0x65/3", "0x69/0", "0x69/1", "0x69/2", "0x67/0", "0x67/2" };
            Assert.All(phone.Received, p => Assert.Contains(p, reading));
            Assert.Equal("download", phone.Restarted);
        }

        [Theory]
        [InlineData(500)]
        [InlineData(1000)]
        [InlineData(501)]
        [InlineData(28 + 132)]
        public void PITs_of_any_size_come_whole(int size)
        {
            byte[] pit = new byte[size];
            new Random(size).NextBytes(pit);
            OdinPacket.PutInt(pit, 0, SamsungPit.Magic);
            OdinPacket.PutInt(pit, 4, (size - 28) / 132);

            foreach (bool pad in new[] { false, true })
            {
                FakeOdinDevice phone = new FakeOdinDevice(pit) { PadLastBlock = pad };
                OdinClient client = new OdinClient(phone);
                client.Handshake();
                client.BeginSession();
                Assert.Equal(pit, client.DumpPit());
                // Still in step: the next answer is the one for the next packet.
                Assert.NotNull(client.DumpDeviceInfo());
            }
        }

        [Fact]
        public void A_phone_that_answers_something_else_than_LOKE_is_not_spoken_to()
        {
            FakeOdinDevice phone = new FakeOdinDevice { HandshakeAnswer = "NOPE" };
            OdinException e = Assert.Throws<OdinException>(() => OdinReader.Read(phone, OdinReboot.Normal));
            Assert.Contains("NOPE", e.Message);
            Assert.Empty(phone.Received);
        }

        [Fact]
        public void A_silent_phone_times_out()
        {
            FakeOdinDevice phone = new FakeOdinDevice { HandshakeAnswer = "" };
            Assert.Throws<OdinException>(() => new OdinClient(phone).Handshake());
        }

        [Fact]
        public void An_old_bootloader_answering_0_is_protocol_1_without_LZ4()
        {
            FakeOdinDevice phone = new FakeOdinDevice { SessionAnswer = 0 };
            OdinReading reading = OdinReader.Read(phone, OdinReboot.None);
            Assert.Equal(1, reading.ProtocolVersion);
            Assert.False(reading.TakesLz4);
            Assert.Null(phone.Restarted);
            Assert.Equal("0x67/0", phone.Received.Last());
        }

        [Fact]
        public void A_bootloader_without_device_information_still_gives_its_PIT()
        {
            FakeOdinDevice phone = new FakeOdinDevice { DeviceInfo = null };
            OdinReading reading = OdinReader.Read(phone, OdinReboot.Normal);
            Assert.NotNull(reading.Pit);
            Assert.Null(reading.Info);
            Assert.Null(reading.InfoProblem);
            Assert.True(reading.Ended);
        }

        [Fact]
        public void A_phone_that_restarts_before_answering_the_restart_is_fine()
        {
            FakeOdinDevice phone = new FakeOdinDevice { RestartsSilently = true };
            OdinReading reading = OdinReader.Read(phone, OdinReboot.Normal);
            Assert.True(reading.Ended);
            Assert.Equal("reboot", phone.Restarted);
        }

        [Fact]
        public void A_PIT_size_out_of_reason_is_refused_and_the_session_still_ends()
        {
            FakeOdinDevice phone = new FakeOdinDevice(new byte[0]);
            Assert.Throws<OdinException>(() => OdinReader.Read(phone, OdinReboot.Normal));
            Assert.Equal(new[] { "0x64/0", "0x65/1", "0x67/0", "0x67/1" }, phone.Received);
        }

        [Fact]
        public void An_unreadable_PIT_fails_the_reading_and_the_session_still_ends()
        {
            FakeOdinDevice phone = new FakeOdinDevice(new byte[600]);
            OdinException e = Assert.Throws<OdinException>(() => OdinReader.Read(phone, OdinReboot.Normal));
            Assert.Contains("PIT", e.Message);
            Assert.Equal("reboot", phone.Restarted);
        }

        [Fact]
        public void A_cable_pulled_in_the_middle_of_the_PIT_is_reported()
        {
            FakeOdinDevice phone = new FakeOdinDevice { SilentFrom = 3 };
            OdinException e = Assert.Throws<OdinException>(() => OdinReader.Read(phone, OdinReboot.Normal));
            Assert.Contains("block", e.Message);
        }

        [Fact]
        public void A_cable_pulled_during_the_device_information_keeps_the_PIT()
        {
            FakeOdinDevice phone = new FakeOdinDevice();
            int pitPackets = 2 + (phone.Pit.Length + 499) / 500 + 1;
            phone.SilentFrom = pitPackets;
            OdinReading reading = OdinReader.Read(phone, OdinReboot.Normal);
            Assert.NotNull(reading.Pit);
            Assert.Null(reading.Info);
            Assert.NotNull(reading.InfoProblem);
            Assert.False(reading.Ended);
        }

        [Fact]
        public void Reading_before_a_session_is_a_mistake_of_the_caller()
        {
            OdinClient client = new OdinClient(new FakeOdinDevice());
            Assert.Throws<InvalidOperationException>(() => client.DumpPit());
            Assert.Throws<InvalidOperationException>(() => client.DumpDeviceInfo());
        }

        [Fact]
        public void The_stand_in_phone_refuses_a_second_session_and_unknown_packets()
        {
            FakeOdinDevice phone = new FakeOdinDevice();
            OdinClient client = new OdinClient(phone);
            client.Handshake();
            client.BeginSession();
            Assert.Throws<OdinException>(() => client.BeginSession());

            // A write request (0x66) is refused by the stand-in like any packet it does not know.
            phone.Write(OdinPacket.Command(OdinPacket.TypeFile, 0), 0, OdinPacket.CommandSize, 1000);
            byte[] answer = new byte[8];
            Assert.Equal(8, phone.Read(answer, 0, 8, 1000));
            Assert.Equal(0xFF, answer[0]);
        }
    }
}
