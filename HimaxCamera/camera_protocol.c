#include "camera_protocol.h"
#include <string.h>

static void put_word(uint8_t *data, uint16_t value)
{
    data[0] = (uint8_t)(value >> 8);
    data[1] = (uint8_t)value;
}

uint16_t camera_crc16(const uint8_t *data, uint16_t size)
{
    uint16_t crc = 0xFFFFU;
    uint8_t bit;
    while (size--)
    {
        crc ^= (uint16_t)(*data++) << 8;
        for (bit = 0; bit < 8; bit++)
            crc = (uint16_t)((crc << 1) ^ ((crc & 0x8000U) ? 0x1021U : 0U));
    }
    return crc;
}

void camera_session_init(camera_session_t *session)
{
    memset(session, 0, sizeof(*session));
    session->selected_chunk = CAMERA_STATUS_CHUNK;
}

uint8_t camera_command(camera_session_t *session, uint16_t command)
{
    session->selected_chunk = CAMERA_STATUS_CHUNK;
    if ((command & 0xFE00U) == CAMERA_CAPTURE_CMD)
    {
        if (session->has_request && command == session->capture_command)
            return 0;
        session->capture_command = command;
        session->has_request = 1;
        session->has_frame = 0;
        session->state = CAMERA_BUSY;
        session->error = CAMERA_OK;
        return 1;
    }
    if (command == CAMERA_STATUS_CMD)
        return 0;
    if (command < CAMERA_CHUNK_COUNT && session->has_frame)
    {
        session->selected_chunk = command;
        session->state = CAMERA_READY;
        session->error = CAMERA_OK;
        return 0;
    }
    session->state = CAMERA_ERROR;
    session->error = command < CAMERA_CHUNK_COUNT ? CAMERA_NO_FRAME : CAMERA_BAD_COMMAND;
    return 0;
}

void camera_complete(camera_session_t *session, camera_error_t error, const uint8_t *image)
{
    session->error = error;
    session->selected_chunk = CAMERA_STATUS_CHUNK;
    session->has_frame = error == CAMERA_OK;
    session->state = error == CAMERA_OK ? CAMERA_READY : CAMERA_ERROR;
    if (error == CAMERA_OK)
    {
        session->frame_id++;
        session->checksum = camera_crc16(image, CAMERA_IMAGE_SIZE);
    }
}

void camera_window(const camera_session_t *session, const uint8_t *image, uint8_t *window)
{
    uint16_t offset;
    uint16_t count;
    memset(window, 0, CAMERA_WINDOW_SIZE);
    window[0] = CAMERA_TAG_ID;
    window[1] = CAMERA_PROTOCOL;
    window[2] = (uint8_t)session->state;
    window[3] = (uint8_t)session->error;
    put_word(window + 4, session->frame_id);
    put_word(window + 6, session->selected_chunk);
    put_word(window + 8, CAMERA_IMAGE_SIZE);
    window[11] = (uint8_t)session->capture_command;
    if (session->selected_chunk == CAMERA_STATUS_CHUNK)
    {
        window[10] = 8;
        put_word(window + 12, CAMERA_COLUMNS);
        put_word(window + 14, CAMERA_ROWS);
        put_word(window + 16, session->has_frame ? session->checksum : 0);
        window[18] = CAMERA_CHUNK_SIZE;
        window[19] = (uint8_t)((session->capture_command & CAMERA_TEST_PATTERN) != 0);
    }
    else if (session->state == CAMERA_READY && session->has_frame)
    {
        offset = session->selected_chunk * CAMERA_CHUNK_SIZE;
        count = CAMERA_IMAGE_SIZE - offset;
        if (count > CAMERA_CHUNK_SIZE)
            count = CAMERA_CHUNK_SIZE;
        window[10] = (uint8_t)count;
        memcpy(window + CAMERA_HEADER_SIZE, image + offset, count);
    }
}
