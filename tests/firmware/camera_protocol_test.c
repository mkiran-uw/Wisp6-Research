/* deeptest: portable camera command, memory-boundary and wire-contract tests. */
#include "../../HimaxCamera/camera_protocol.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>

static uint8_t guarded_image[CAMERA_IMAGE_SIZE + 2];
static uint8_t guarded_window[CAMERA_WINDOW_SIZE + 2];

static uint16_t word(const uint8_t *data)
{
    return (uint16_t)(((uint16_t)data[0] << 8) | data[1]);
}

int main(int argc, char **argv)
{
    camera_session_t session;
    uint8_t *image = guarded_image + 1;
    uint8_t *window = guarded_window + 1;
    uint16_t chunk, i, offset, count;
    FILE *fixture = NULL;

    memset(guarded_image, 0xA5, sizeof(guarded_image));
    memset(guarded_window, 0xA5, sizeof(guarded_window));
    for (i = 0; i < CAMERA_IMAGE_SIZE; i++)
        image[i] = (uint8_t)(i * 17U + 3U);
    assert(camera_crc16((const uint8_t *)"123456789", 9) == 0x29B1);
    assert(camera_crc16(image, 0) == 0xFFFF);

    camera_session_init(&session);
    camera_window(&session, image, window);
    assert(window[0] == 0xCA && window[1] == 1 && window[2] == CAMERA_IDLE);
    assert(word(window + 6) == CAMERA_STATUS_CHUNK && word(window + 8) == CAMERA_IMAGE_SIZE);
    assert(word(window + 12) == 162 && word(window + 14) == 122);
    assert(word(window + 16) == 0 && window[18] == 20 && window[19] == 0);
    assert(camera_command(&session, CAMERA_STATUS_CMD) == 0);
    assert(camera_command(&session, 0) == 0 && session.error == CAMERA_NO_FRAME);
    assert(camera_command(&session, CAMERA_CHUNK_COUNT) == 0 && session.error == CAMERA_BAD_COMMAND);
    assert(camera_command(&session, 0x8200) == 0 && session.error == CAMERA_BAD_COMMAND);

    assert(camera_command(&session, 0x802A) == 1 && session.state == CAMERA_BUSY);
    assert(camera_command(&session, 0x802A) == 0 && session.state == CAMERA_BUSY);
    camera_complete(&session, CAMERA_I2C_NACK, image);
    camera_window(&session, image, window);
    assert(window[2] == CAMERA_ERROR && window[3] == CAMERA_I2C_NACK && window[11] == 42);
    assert(session.frame_id == 0 && !session.has_frame);
    assert(camera_command(&session, 0x802A) == 0 && session.error == CAMERA_I2C_NACK);
    assert(camera_command(&session, 0x802B) == 1);
    camera_complete(&session, CAMERA_OK, image);
    assert(session.frame_id == 1 && session.has_frame && session.state == CAMERA_READY);
    assert(camera_command(&session, 0x802B) == 0 && session.frame_id == 1);
    camera_window(&session, image, window);
    assert(word(window + 16) == camera_crc16(image, CAMERA_IMAGE_SIZE));
    if (argc == 2)
    {
        fixture = fopen(argv[1], "wb");
        assert(fixture != NULL);
        assert(fwrite(window, 1, CAMERA_WINDOW_SIZE, fixture) == CAMERA_WINDOW_SIZE);
    }
    for (chunk = 0; chunk < CAMERA_CHUNK_COUNT; chunk++)
    {
        assert(camera_command(&session, chunk) == 0);
        camera_window(&session, image, window);
        offset = chunk * CAMERA_CHUNK_SIZE;
        count = CAMERA_IMAGE_SIZE - offset;
        if (count > CAMERA_CHUNK_SIZE)
            count = CAMERA_CHUNK_SIZE;
        assert(word(window + 4) == 1 && word(window + 6) == chunk);
        assert(window[10] == count && window[11] == 43);
        assert(memcmp(window + CAMERA_HEADER_SIZE, image + offset, count) == 0);
        for (i = CAMERA_HEADER_SIZE + count; i < CAMERA_WINDOW_SIZE; i++)
            assert(window[i] == 0);
        assert(guarded_image[0] == 0xA5 && guarded_image[CAMERA_IMAGE_SIZE + 1] == 0xA5);
        assert(guarded_window[0] == 0xA5 && guarded_window[CAMERA_WINDOW_SIZE + 1] == 0xA5);
        if (fixture)
            assert(fwrite(window, 1, CAMERA_WINDOW_SIZE, fixture) == CAMERA_WINDOW_SIZE);
    }
    assert(window[10] == 4);
    if (fixture)
        assert(fclose(fixture) == 0);
    assert(camera_command(&session, CAMERA_STATUS_CMD) == 0);
    camera_window(&session, image, window);
    assert(word(window + 6) == CAMERA_STATUS_CHUNK && session.frame_id == 1);
    assert(camera_command(&session, 0x4001) == 0 && session.error == CAMERA_BAD_COMMAND);
    assert(camera_command(&session, 0) == 0 && session.state == CAMERA_READY && session.error == CAMERA_OK);
    assert(camera_command(&session, 0x812B) == 1 && !session.has_frame);
    camera_window(&session, image, window);
    assert(window[19] == 1 && word(window + 16) == 0);
    session.frame_id = 0xFFFF;
    camera_complete(&session, CAMERA_OK, image);
    assert(session.frame_id == 0);
    assert(camera_command(&session, 0x802C) == 1);
    camera_complete(&session, CAMERA_FRAME_TIMEOUT, image);
    assert(session.state == CAMERA_ERROR && !session.has_frame);
    assert(camera_command(&session, 1) == 0 && session.error == CAMERA_NO_FRAME);
    puts("Firmware protocol tests passed: all 989 chunks, tail bounds, replay, errors and CRC.");
    return 0;
}
