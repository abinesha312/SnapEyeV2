import websockets
import asyncio
import json

async def transcribe():
    token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0dXNlciIsImFwaV9rZXlfaGFzaCI6ImQ0YjNiMDhlNGQxMzFlNzMiLCJleHAiOjE3NjMwMDAwOTEsImlhdCI6MTc2Mjk5NjQ5MSwianRpIjoiTzhEa29IZXlNczFLX1NyMGJDM2FGUSJ9.i9DDZNDCd_q5TDTCnT_ROK7zFt0ZnDnqMxO6EsK8a1Y"
    uri = f"ws://localhost:8080/api/transcribe/live?token={token}&voice=alloy"

    async with websockets.connect(uri) as websocket:
        # Wait for session creation
        response = await websocket.recv()
        print(f"Connected: {response}")

        # Send audio data
        await websocket.send(json.dumps({
            "type": "input_audio_buffer.append",
            "audio": "base64_audio_data_here"
        }))

        # Receive transcriptions
        while True:
            message = await websocket.recv()
            print(f"Received: {message}")

asyncio.run(transcribe())